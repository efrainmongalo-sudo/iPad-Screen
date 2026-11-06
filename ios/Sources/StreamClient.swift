import Foundation
import Network
import CoreGraphics

protocol StreamClientDelegate: AnyObject {
    /// Llega siempre en el hilo principal.
    func streamClient(_ client: StreamClient, didReceiveFrame data: Data)
    func streamClient(_ client: StreamClient, didChangeState text: String, connected: Bool)
}

/// Cliente TCP. Protocolo:
///  - PC -> iPad: [4 bytes longitud big-endian][datos JPEG] repetido.
///  - iPad -> PC: líneas de texto "D x y", "M x y" o "U x y" (x, y entre 0 y 1).
final class StreamClient {
    weak var delegate: StreamClientDelegate?

    private let queue = DispatchQueue(label: "monitor.stream")
    private var connection: NWConnection?
    private var buffer = Data()
    private let maxFrame = 16 * 1024 * 1024

    func connect(host: String, port: UInt16) {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.closeLocked()
            guard let nwPort = NWEndpoint.Port(rawValue: port) else { return }

            let params = NWParameters.tcp
            if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
                tcp.noDelay = true
            }
            let conn = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: params)
            self.connection = conn

            conn.stateUpdateHandler = { [weak self, weak conn] state in
                guard let self = self, let conn = conn, conn === self.connection else { return }
                switch state {
                case .ready:
                    self.notify("Conectado", connected: true)
                    self.receive(on: conn)
                case .waiting(let error):
                    self.notify("Esperando conexión… \(error.localizedDescription)", connected: false)
                case .failed(let error):
                    self.fail("Error de conexión: \(error.localizedDescription)")
                default:
                    break
                }
            }
            conn.start(queue: self.queue)
        }
    }

    func disconnect() {
        queue.async { [weak self] in
            self?.closeLocked()
        }
    }

    func sendTouch(_ phase: String, x: CGFloat, y: CGFloat) {
        let line = "\(phase) \(String(format: "%.4f", Double(x))) \(String(format: "%.4f", Double(y)))\n"
        queue.async { [weak self] in
            guard let conn = self?.connection, let data = line.data(using: .utf8) else { return }
            conn.send(content: data, completion: .contentProcessed({ _ in }))
        }
    }

    // MARK: - Interno (siempre en `queue`)

    private func closeLocked() {
        connection?.cancel()
        connection = nil
        buffer.removeAll()
    }

    private func fail(_ text: String) {
        closeLocked()
        notify(text, connected: false)
    }

    private func receive(on conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, isComplete, error in
            guard let self = self, conn === self.connection else { return }
            if let data = data, !data.isEmpty {
                self.buffer.append(data)
                self.drain()
            }
            if error != nil || isComplete {
                self.fail("Conexión cerrada por la PC")
                return
            }
            self.receive(on: conn)
        }
    }

    /// Extrae tramas completas; si hay varias acumuladas solo entrega la más reciente
    /// para mantener baja la latencia.
    private func drain() {
        var latest: Data?
        while buffer.count >= 4 {
            let length = buffer.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
            if length <= 0 || length > maxFrame {
                fail("Trama inválida recibida")
                return
            }
            if buffer.count < 4 + length { break }
            latest = buffer.subdata(in: 4..<(4 + length))
            buffer.removeSubrange(0..<(4 + length))
        }
        if let frame = latest {
            DispatchQueue.main.async { [weak self] in
                guard let self = self else { return }
                self.delegate?.streamClient(self, didReceiveFrame: frame)
            }
        }
    }

    private func notify(_ text: String, connected: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            self.delegate?.streamClient(self, didChangeState: text, connected: connected)
        }
    }
}
