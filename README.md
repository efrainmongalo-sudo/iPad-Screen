# iPad Air como monitor secundario de Windows

Dos partes:

- `ios/`: app para la iPad (Swift/UIKit, iOS 12). GitHub Actions genera el `.ipa` sin firmar.
- `windows/`: programa de Windows en C# (.NET 8) con ventana, icono junto al reloj e instalador. GitHub Actions genera `MonitorSecundario-Setup.exe`.

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

1. Ejecuta `MonitorSecundario-Setup.exe` y sigue el asistente (está en español).
2. Opcional: marca *Iniciar con Windows* para que se abra solo, junto al reloj, y empiece a transmitir.
3. El instalador crea una regla de firewall solo para redes privadas y solo para equipos de tu misma red local.
4. Si Windows marca tu WiFi como red *pública*, cámbiala a *privada* en Configuración > Red e Internet.

Windows puede mostrar un aviso de SmartScreen porque el programa no está firmado digitalmente: *Más información > Ejecutar de todas formas*.

## 4. Instalar la app en la iPad con Sideloadly

1. Instala iTunes (la versión de apple.com, no la de Microsoft Store) y Sideloadly.
2. Conecta la iPad por USB y toca *Confiar* en ella.
3. En Sideloadly, arrastra `MonitorSecundario.ipa`, escribe tu Apple ID y pulsa *Start*.
4. En la iPad: Ajustes > General > Administración de dispositivos (o Perfiles) > confía en tu Apple ID.
5. Con un Apple ID gratuito la app caduca a los 7 días; se reinstala igual desde Sideloadly.

## 5. Usarlo

1. Abre **Monitor secundario** en la PC y pulsa *Iniciar transmisión*. La ventana muestra la IP que debes escribir en la iPad.
2. PC e iPad deben estar en la misma red WiFi.
3. En la app de la iPad escribe esa IP y el puerto (5800) y toca *Conectar*. El engranaje de la esquina superior izquierda vuelve a abrir la pantalla de conexión.
4. Al cerrar la ventana con la X, el programa sigue transmitiendo junto al reloj. Para salir del todo: clic derecho en su icono > *Salir*.

## Protocolo

- PC a iPad: `[4 bytes de longitud, big-endian][imagen JPEG]`, repetido.
- iPad a PC: líneas de texto `D x y`, `M x y` y `U x y` (pulsar, mover, soltar), con x e y entre 0 y 1.

## Limitaciones de esta versión

- No hay contraseña: cualquier equipo de la misma red local que conozca la IP y el puerto puede ver la pantalla y mover el ratón mientras la transmisión está activa. Úsalo solo en redes de confianza.
- La captura usa GDI (`CopyFromScreen`): sirve para validar, pero no es la más rápida ni muestra el cursor. El siguiente paso es cambiarla por Desktop Duplication (DXGI).
- Para que la iPad sea un monitor extendido (y no solo una copia) hace falta un driver de pantalla virtual; al instalarlo aparece en la lista *Pantalla que se comparte*.
- El ratón no puede controlar ventanas abiertas «como administrador» si el programa corre sin esos permisos.
- Solo WiFi. Por USB haría falta usbmuxd.
- La app de la iPad no lleva icono.
