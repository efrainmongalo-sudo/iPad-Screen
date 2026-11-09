import Foundation
import Network
import CoreGraphics
import UIKit

protocol StreamServerDelegate: AnyObject {
    /// Llega siempre en el hilo principal.
    func streamServer(_ server: StreamServer, didReceiveFrame data: Data)
    func streamServer(_ server: StreamServer, didChangeState text: String, connected: Bool)
}

/// Servidor TCP que escucha en la iPad. La PC se conecta por el cable USB (servicio usbmuxd de iTunes).
/// Protocolo:
///  - PC -> iPad: [4 bytes longitud big-endian][datos JPEG] repetido.
///  - iPad -> PC: líneas de texto "D x y", "M x y" o "U x y" (x, y entre 0 y 1) para los toques,
///    y "A" por cada cuadro recibido (confirmación para que la PC no acumule retraso).
final class StreamServer {
    static let port: UInt16 = 5800

    weak var delegate: StreamServerDelegate?

    private let queue = DispatchQueue(label: "monitor.stream")
    private var listener: NWListener?
    private var listenerReady = false
    private var connection: NWConnection?
    private var buffer = Data()
    private let maxFrame = 16 * 1024 * 1024

    func start() {
        queue.async { [weak self] in
            self?.startLocked()
        }
    }

    /// Al volver a primer plano, iOS puede haber cerrado el puerto: se reabre si hace falta.
    func restartIfNeeded() {
        queue.async { [weak self] in
            guard let self = self, !self.listenerReady else { return }
            self.stopListenerLocked()
            self.startLocked()
        }
    }

    func sendTouch(_ phase: String, x: CGFloat, y: CGFloat) {
        let line = "\(phase) \(String(format: "%.4f", Double(x))) \(String(format: "%.4f", Double(y)))\n"
        send(line)
    }

    /// Informa a la PC del tamaño de la pantalla (píxeles y puntos) para que ajuste el monitor virtual.
    /// Formato: "R anchoPx altoPx anchoPuntos altoPuntos pad|phone".
    func sendScreenSize(points: CGSize, scale: CGFloat) {
        let pointsW = Int(points.width.rounded())
        let pointsH = Int(points.height.rounded())
        let pixelsW = Int((points.width * scale).rounded())
        let pixelsH = Int((points.height * scale).rounded())
        let idiom = UIDevice.current.userInterfaceIdiom == .pad ? "pad" : "phone"
        send("R \(pixelsW) \(pixelsH) \(pointsW) \(pointsH) \(idiom)\n")
    }

    // MARK: - Interno (siempre en `queue`)

    private func send(_ text: String) {
        queue.async { [weak self] in
            guard let conn = self?.connection, let data = text.data(using: .utf8) else { return }
            conn.send(content: data, completion: .contentProcessed({ _ in }))
        }
    }

    private func startLocked() {
        guard listener == nil else { return }

        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true
        }

        guard let port = NWEndpoint.Port(rawValue: StreamServer.port),
              let newListener = try? NWListener(using: params, on: port) else {
            notify("No se pudo abrir el puerto \(StreamServer.port)", connected: false)
            retryLater()
            return
        }
        listener = newListener

        newListener.stateUpdateHandler = { [weak self, weak newListener] state in
            guard let self = self, let current = newListener, current === self.listener else { return }
            switch state {
            case .ready:
                self.listenerReady = true
                if self.connection == nil {
                    self.notify("Esperando a la PC…", connected: false)
                }
            case .failed(let error):
                self.stopListenerLocked()
                self.notify("Error: \(error.localizedDescription)", connected: false)
                self.retryLater()
            default:
                break
            }
        }
        newListener.newConnectionHandler = { [weak self] conn in
            self?.accept(conn)
        }
        newListener.start(queue: queue)
    }

    private func stopListenerLocked() {
        listener?.cancel()
        listener = nil
        listenerReady = false
    }

    private func retryLater() {
        queue.asyncAfter(deadline: .now() + 1) { [weak self] in
            self?.startLocked()
        }
    }

    private func accept(_ conn: NWConnection) {
        dropConnectionLocked() // una sola PC a la vez
        connection = conn
        buffer.removeAll()

        conn.stateUpdateHandler = { [weak self, weak conn] state in
            guard let self = self, let conn = conn, conn === self.connection else { return }
            switch state {
            case .ready:
                self.notify("Conectado", connected: true)
                self.receive(on: conn)
            case .failed:
                self.connectionLost()
            default:
                break
            }
        }
        conn.start(queue: queue)
    }

    private func dropConnectionLocked() {
        let old = connection
        connection = nil
        buffer.removeAll()
        old?.cancel()
    }

    private func connectionLost() {
        dropConnectionLocked()
        notify("Esperando a la PC…", connected: false)
    }

    private func receive(on conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, isComplete, error in
            guard let self = self, conn === self.connection else { return }
            if let data = data, !data.isEmpty {
                self.buffer.append(data)
                self.drain()
            }
            if error != nil || isComplete {
                self.connectionLost()
                return
            }
            self.receive(on: conn)
        }
    }

    /// Extrae las tramas completas y confirma cada una a la PC. Si hay varias acumuladas
    /// solo entrega la más reciente, para no sumar retraso.
    private func drain() {
        var latest: Data?
        var received = 0
        while buffer.count >= 4 {
            let length = buffer.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
            if length <= 0 || length > maxFrame {
                connectionLost()
                return
            }
            if buffer.count < 4 + length { break }
            latest = buffer.subdata(in: 4..<(4 + length))
            buffer.removeSubrange(0..<(4 + length))
            received += 1
        }
        if received > 0 {
            send(String(repeating: "A\n", count: received))
        }
        if let frame = latest {
            DispatchQueue.main.async { [weak self] in
                guard let self = self else { return }
                self.delegate?.streamServer(self, didReceiveFrame: frame)
            }
        }
    }

    private func notify(_ text: String, connected: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            self.delegate?.streamServer(self, didChangeState: text, connected: connected)
        }
    }
}
