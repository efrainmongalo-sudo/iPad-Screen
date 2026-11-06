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

final class ViewController: UIViewController, StreamClientDelegate {
    private let screenView = ScreenView()
    private let client = StreamClient()

    private let overlay = UIView()
    private let hostField = UITextField()
    private let portField = UITextField()
    private let connectButton = UIButton(type: .system)
    private let statusLabel = UILabel()
    private let gearButton = UIButton(type: .system)

    private let decodeQueue = DispatchQueue(label: "monitor.decode")
    private var decoding = false
    private var pendingFrame: Data?
    private let defaults = UserDefaults.standard

    override var prefersStatusBarHidden: Bool { return true }
    override var shouldAutorotate: Bool { return true }
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { return .all }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        client.delegate = self

        setupScreen()
        setupOverlay()
        setupGear()

        hostField.text = defaults.string(forKey: "host") ?? ""
        let savedPort = defaults.integer(forKey: "port")
        portField.text = String(savedPort == 0 ? 5800 : savedPort)

        if let host = hostField.text, !host.isEmpty {
            connect()
        }
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
            self?.client.sendTouch(phase, x: point.x, y: point.y)
        }
    }

    private func styleField(_ field: UITextField, placeholder: String, keyboard: UIKeyboardType) {
        field.placeholder = placeholder
        field.keyboardType = keyboard
        field.borderStyle = .roundedRect
        field.autocorrectionType = .no
        field.autocapitalizationType = .none
        field.clearButtonMode = .whileEditing
        field.heightAnchor.constraint(equalToConstant: 44).isActive = true
    }

    private func setupOverlay() {
        overlay.backgroundColor = UIColor(white: 0, alpha: 0.88)
        pinToEdges(overlay)
        let dismissKeyboard = UITapGestureRecognizer(target: view, action: #selector(UIView.endEditing(_:)))
        dismissKeyboard.cancelsTouchesInView = false
        overlay.addGestureRecognizer(dismissKeyboard)

        let title = UILabel()
        title.text = "Monitor secundario"
        title.textColor = .white
        title.font = UIFont.boldSystemFont(ofSize: 26)
        title.textAlignment = .center

        styleField(hostField, placeholder: "IP de la PC (ej. 192.168.1.10)", keyboard: .numbersAndPunctuation)
        styleField(portField, placeholder: "Puerto", keyboard: .numberPad)

        connectButton.setTitle("Conectar", for: .normal)
        connectButton.titleLabel?.font = UIFont.boldSystemFont(ofSize: 20)
        connectButton.setTitleColor(.white, for: .normal)
        connectButton.backgroundColor = UIColor(red: 0.10, green: 0.50, blue: 0.90, alpha: 1)
        connectButton.layer.cornerRadius = 8
        connectButton.heightAnchor.constraint(equalToConstant: 48).isActive = true
        connectButton.addTarget(self, action: #selector(connectTapped), for: .touchUpInside)

        statusLabel.text = "Desconectado"
        statusLabel.textColor = .lightGray
        statusLabel.textAlignment = .center
        statusLabel.numberOfLines = 0

        let stack = UIStackView(arrangedSubviews: [title, hostField, portField, connectButton, statusLabel])
        stack.axis = .vertical
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        overlay.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: overlay.centerXAnchor),
            stack.topAnchor.constraint(equalTo: overlay.topAnchor, constant: 60),
            stack.widthAnchor.constraint(equalToConstant: 340)
        ])
    }

    private func setupGear() {
        gearButton.setTitle("⚙︎", for: .normal)
        gearButton.titleLabel?.font = UIFont.systemFont(ofSize: 28)
        gearButton.setTitleColor(.white, for: .normal)
        gearButton.alpha = 0.35
        gearButton.isHidden = true
        gearButton.translatesAutoresizingMaskIntoConstraints = false
        gearButton.addTarget(self, action: #selector(gearTapped), for: .touchUpInside)
        view.addSubview(gearButton)
        NSLayoutConstraint.activate([
            gearButton.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 8),
            gearButton.topAnchor.constraint(equalTo: view.topAnchor, constant: 8),
            gearButton.widthAnchor.constraint(equalToConstant: 44),
            gearButton.heightAnchor.constraint(equalToConstant: 44)
        ])
    }

    // MARK: - Acciones

    @objc private func connectTapped() {
        view.endEditing(true)
        connect()
    }

    @objc private func gearTapped() {
        overlay.isHidden = false
        gearButton.isHidden = true
    }

    private func connect() {
        let host = (hostField.text ?? "").trimmingCharacters(in: .whitespaces)
        guard !host.isEmpty, let port = UInt16(portField.text ?? ""), port > 0 else {
            statusLabel.text = "Escribe una IP y un puerto válidos"
            return
        }
        defaults.set(host, forKey: "host")
        defaults.set(Int(port), forKey: "port")
        statusLabel.text = "Conectando a \(host):\(port)…"
        client.connect(host: host, port: port)
    }

    // MARK: - StreamClientDelegate

    func streamClient(_ client: StreamClient, didChangeState text: String, connected: Bool) {
        statusLabel.text = text
        overlay.isHidden = connected
        gearButton.isHidden = !connected
    }

    func streamClient(_ client: StreamClient, didReceiveFrame data: Data) {
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
