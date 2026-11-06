# iPad Air como monitor secundario de Windows (por cable USB)

Dos partes:

- `ios/`: app para la iPad (Swift/UIKit, iOS 12). GitHub Actions genera el `.ipa` sin firmar.
- `windows/`: programa de Windows en C# (.NET 8) con ventana, icono junto al reloj e instalador. GitHub Actions genera `MonitorSecundario-Setup.exe`.

La comunicación es **solo por el cable USB**: no necesita WiFi ni red. La app de la iPad escucha en el puerto 5800 y el programa de Windows se conecta por el servicio de iTunes (*Apple Mobile Device Service*).

## Requisitos

- iTunes de apple.com (no la versión de Microsoft Store) instalado en la PC. Ya lo tienes si usas Sideloadly.
- Cable USB de la iPad.

## 1. Subir cambios a GitHub

Desde la carpeta del proyecto, en PowerShell:

```powershell
git add .
git commit -m "Describe aquí el cambio"
git push
```

Cada push compila solo la parte que cambió (`ios/` o `windows/`). También se puede lanzar a mano desde la pestaña **Actions**, con *Run workflow*.

## 2. Descargar los resultados

En **Actions**, entra a la ejecución terminada y baja de *Artifacts*:

- `MonitorSecundario-Instalador`: contiene `MonitorSecundario-Setup.exe`, el instalador de Windows.
- `MonitorServidor-portable`: el programa suelto, sin instalar.
- `MonitorSecundario-ipa`: contiene `MonitorSecundario.ipa`, la app de la iPad.

Todos vienen dentro de un .zip: extráelo primero.

## 3. Instalar el programa en la PC

1. Ejecuta `MonitorSecundario-Setup.exe` y sigue el asistente (está en español). Si falta iTunes, te avisa.
2. Opcional: marca *Iniciar con Windows* para que se abra solo, junto al reloj, y empiece a buscar la iPad.

Windows puede mostrar un aviso de SmartScreen porque el programa no está firmado digitalmente: *Más información > Ejecutar de todas formas*.

## 4. Instalar la app en la iPad con Sideloadly

1. Conecta la iPad por USB y toca *Confiar* en ella.
2. En Sideloadly, arrastra `MonitorSecundario.ipa`, escribe tu Apple ID y pulsa *Start*. Si el inicio de sesión falla, prueba otro tipo en *Settings > Anisette Authentication*.
3. En la iPad: Ajustes > General > Administración de dispositivos (o Perfiles) > confía en tu Apple ID.
4. Con un Apple ID gratuito la app caduca a los 7 días; se reinstala igual desde Sideloadly.

## 5. Usarlo

1. **Una sola vez:** en la ventana de Windows, en la tarjeta *Pantalla virtual*, pulsa *Instalar y configurar*. Acepta el permiso de administrador. Instala con winget el driver «Virtual Display Driver» (firmado) y registra los tamaños de iPad en horizontal y vertical. Si el monitor no aparece en Windows, reinicia la laptop una vez.
2. Conecta la iPad con el cable y abre la app **Monitor PC** (déjala en primer plano).
3. En la PC abre **Monitor secundario**, deja el modo *Monitor extendido* y pulsa *Iniciar transmisión*.
4. Windows muestra un monitor adicional con la resolución de la iPad (en Configuración > Sistema > Pantalla). Arrastra ventanas hacia él.
5. Al girar la iPad, la app avisa su nuevo tamaño y Windows cambia la resolución del monitor virtual (horizontal ↔ vertical) sola.
6. Modo *Duplicar la pantalla principal*: no usa el driver; solo copia la pantalla de la laptop.
7. Al cerrar la ventana con la X, el programa sigue junto al reloj. Para salir del todo: clic derecho en su icono > *Salir*.

Detalle de imagen: *Rápido* usa el tamaño en puntos de la iPad (1024×768 en iPad Air, menos retraso); *Nítido* usa los píxeles reales (2048×1536, más datos y más retraso).

## Cómo se mantiene el retraso bajo

- USB en lugar de WiFi: sin interferencias ni variaciones de la red.
- Control de flujo: la iPad confirma cada cuadro que recibe y la PC nunca tiene más de 2 sin confirmar, así no se acumulan cuadros viejos.
- Si llegan varios cuadros a la vez, la iPad solo muestra el más reciente.
- Ajustes: bajar la resolución máxima o la calidad reduce el tiempo de compresión y de decodificación.

## Protocolo

- La iPad escucha en el puerto 5800. La PC abre el túnel por usbmuxd (`127.0.0.1:27015`).
- PC a iPad: `[4 bytes de longitud, big-endian][imagen JPEG]`, repetido.
- iPad a PC: líneas de texto `D x y`, `M x y` y `U x y` (pulsar, mover, soltar, con x e y entre 0 y 1), `A` por cada cuadro recibido y `R pxAncho pxAlto ptAncho ptAlto` con el tamaño de la pantalla (al conectar y al girar).

## Limitaciones de esta versión

- La captura usa GDI (`CopyFromScreen`): sirve para validar, pero es la parte más lenta y no muestra el cursor. El siguiente paso para bajar el retraso es cambiarla por Desktop Duplication (DXGI).
- El monitor virtual usa un driver de terceros (VirtualDrivers/Virtual-Display-Driver). Los tamaños se leen de `C:\VirtualDisplayDriver\vdd_settings.xml`; si el driver no los toma, reinicia Windows. Si la iPad pide un tamaño que no existe en la lista, se usa el más parecido.
- Al detener la transmisión el monitor virtual sigue existiendo en Windows (se puede desactivar en Configuración > Pantalla).
- El ratón no puede controlar ventanas abiertas «como administrador» si el programa corre sin esos permisos.
- La app de la iPad no lleva icono.
