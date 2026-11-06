import UIKit
import ImageIO

/// Vista que muestra la imagen de la PC y convierte los toques en coordenadas 0...1.
final class ScreenView: UIView {
    var onTouch: ((String, CGPoint) -> Void)?
    private var imageSize = CGSize.zero

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        layer.contentsGravity = .resizeAspect
        isMultipleTouchEnabled = false
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) no implementado")
    }

    func display(_ image: CGImage) {
        layer.contents = image
        imageSize = CGSize(width: image.width, height: image.height)
    }

    /// Zona donde realmente se dibuja la imagen (resizeAspect deja barras negras).
    private func contentRect() -> CGRect {
        guard imageSize.width > 0, imageSize.height > 0, bounds.width > 0, bounds.height > 0 else {
            return bounds
        }
        let scale = min(bounds.width / imageSize.width, bounds.height / imageSize.height)
        let w = imageSize.width * scale
        let h = imageSize.height * scale
        return CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
    }

    private func normalized(_ touch: UITouch) -> CGPoint {
        let p = touch.location(in: self)
        let r = contentRect()
        let x = min(max((p.x - r.minX) / r.width, 0), 1)
        let y = min(max((p.y - r.minY) / r.height, 0), 1)
        return CGPoint(x: x, y: y)
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let t = touches.first { onTouch?("D", normalized(t)) }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let t = touches.first { onTouch?("M", normalized(t)) }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let t = touches.first { onTouch?("U", normalized(t)) }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let t = touches.first { onTouch?("U", normalized(t)) }
    }
}

final class ViewController: UIViewController, StreamServerDelegate {
    private let screenView = ScreenView()
    private let server = StreamServer()

    private let overlay = UIView()
    private let statusLabel = UILabel()

    private let decodeQueue = DispatchQueue(label: "monitor.decode")
    private var decoding = false
    private var pendingFrame: Data?

    private var isConnected = false
    private var lastSentSize = CGSize.zero

    override var prefersStatusBarHidden: Bool { return true }
    override var shouldAutorotate: Bool { return true }
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { return .all }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        server.delegate = self

        setupScreen()
        setupOverlay()

        NotificationCenter.default.addObserver(self,
                                               selector: #selector(appDidBecomeActive),
                                               name: UIApplication.didBecomeActiveNotification,
                                               object: nil)
        server.start()
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    @objc private func appDidBecomeActive() {
        server.restartIfNeeded()
    }

    /// Al girar la iPad cambia el tamaño de la vista: se avisa a la PC para que ajuste el monitor virtual.
    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        sendSizeIfNeeded()
    }

    private func sendSizeIfNeeded() {
        guard isConnected else { return }
        let size = view.bounds.size
        guard size.width > 0, size.height > 0, size != lastSentSize else { return }
        lastSentSize = size
        server.sendScreenSize(points: size, scale: UIScreen.main.nativeScale)
    }

    // MARK: - Interfaz

    private func pinToEdges(_ child: UIView) {
        child.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(child)
        NSLayoutConstraint.activate([
            child.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            child.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            child.topAnchor.constraint(equalTo: view.topAnchor),
            child.bottomAnchor.constraint(equalTo: view.bottomAnchor)
        ])
    }

    private func setupScreen() {
        pinToEdges(screenView)
        screenView.onTouch = { [weak self] phase, point in
            self?.server.sendTouch(phase, x: point.x, y: point.y)
        }
    }

    private func setupOverlay() {
        overlay.backgroundColor = UIColor(red: 0.055, green: 0.07, blue: 0.094, alpha: 1)
        pinToEdges(overlay)

        let title = UILabel()
        title.text = "Monitor secundario"
        title.textColor = .white
        title.font = UIFont.boldSystemFont(ofSize: 30)
        title.textAlignment = .center

        let steps = UILabel()
        steps.text = "1. Conecta esta iPad a la PC con el cable USB.\n2. En Windows abre «Monitor secundario» y pulsa «Iniciar transmisión»."
        steps.textColor = UIColor(white: 0.75, alpha: 1)
        steps.font = UIFont.systemFont(ofSize: 18)
        steps.textAlignment = .center
        steps.numberOfLines = 0

        statusLabel.text = "Iniciando…"
        statusLabel.textColor = UIColor(red: 0.94, green: 0.71, blue: 0.24, alpha: 1)
        statusLabel.font = UIFont.boldSystemFont(ofSize: 20)
        statusLabel.textAlignment = .center
        statusLabel.numberOfLines = 0

        let stack = UIStackView(arrangedSubviews: [title, steps, statusLabel])
        stack.axis = .vertical
        stack.spacing = 24
        stack.translatesAutoresizingMaskIntoConstraints = false
        overlay.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: overlay.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: overlay.centerYAnchor),
            stack.widthAnchor.constraint(lessThanOrEqualToConstant: 560),
            stack.leadingAnchor.constraint(greaterThanOrEqualTo: overlay.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: overlay.trailingAnchor, constant: -24)
        ])
    }

    // MARK: - StreamServerDelegate

    func streamServer(_ server: StreamServer, didChangeState text: String, connected: Bool) {
        statusLabel.text = text
        overlay.isHidden = connected
        isConnected = connected
        if connected {
            lastSentSize = .zero // al conectar siempre se informa el tamaño actual
            sendSizeIfNeeded()
        }
    }

    func streamServer(_ server: StreamServer, didReceiveFrame data: Data) {
        show(data)
    }

    /// Decodifica en segundo plano; si llega otra trama mientras tanto, solo se conserva la última.
    private func show(_ data: Data) {
        if decoding {
            pendingFrame = data
            return
        }
        decoding = true
        decodeQueue.async { [weak self] in
            let options = [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
            var image: CGImage?
            if let source = CGImageSourceCreateWithData(data as CFData, nil) {
                image = CGImageSourceCreateImageAtIndex(source, 0, options)
            }
            DispatchQueue.main.async {
                guard let self = self else { return }
                if let image = image {
                    self.screenView.display(image)
                }
                self.decoding = false
                if let next = self.pendingFrame {
                    self.pendingFrame = nil
                    self.show(next)
                }
            }
        }
    }
}
