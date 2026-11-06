# iPad Air como monitor secundario de Windows

Dos partes:

- `ios/`: app para la iPad (Swift/UIKit, iOS 12). GitHub Actions genera el `.ipa` sin firmar.
- `windows/`: programa de Windows en C# (.NET 8). GitHub Actions genera `MonitorServidor.exe`.

## 1. Subir el proyecto a GitHub

1. Crea un repositorio nuevo en GitHub (puede ser privado).
2. Sube todo el contenido de esta carpeta, incluida `.github/`. Con GitHub Desktop o con git es lo más sencillo.
3. Al subir a la rama `main` se lanzan solos los dos workflows. También puedes lanzarlos a mano desde la pestaña **Actions**, con *Run workflow*.

## 2. Descargar los resultados

En **Actions**, entra a la ejecución terminada y baja de *Artifacts*:

- `MonitorSecundario-ipa`: es un .zip, extráelo para obtener `MonitorSecundario.ipa`.
- `MonitorServidor-exe`: es un .zip, extráelo para obtener `MonitorServidor.exe`.

## 3. Instalar la app en la iPad con Sideloadly

1. Instala iTunes (la versión de apple.com, no la de Microsoft Store) y Sideloadly.
2. Conecta la iPad por USB y toca *Confiar* en ella.
3. En Sideloadly, arrastra `MonitorSecundario.ipa`, escribe tu Apple ID y pulsa *Start*.
4. En la iPad: Ajustes > General > Administración de dispositivos (o Perfiles) > confía en tu Apple ID.
5. Con un Apple ID gratuito la app caduca a los 7 días; se reinstala igual desde Sideloadly.

## 4. Usarlo

1. En la PC ejecuta `MonitorServidor.exe`. Muestra las pantallas y las IP de la PC. Windows pedirá permitir el acceso en el firewall de redes privadas.
2. PC e iPad deben estar en la misma red WiFi.
3. En la app escribe la IP de la PC y el puerto (5800) y toca *Conectar*.
4. El engranaje de la esquina superior izquierda vuelve a abrir la pantalla de conexión.

Opciones del servidor: `--port 5800 --screen 0 --maxwidth 1280 --quality 70 --fps 30 --list`.
`--screen` elige cuál pantalla de Windows se comparte, y con `--list` solo las muestra. Cuando instales un driver de pantalla virtual aparecerá como una pantalla más; así la iPad será un monitor extendido.

## Protocolo

- PC a iPad: `[4 bytes de longitud, big-endian][imagen JPEG]`, repetido.
- iPad a PC: líneas de texto `D x y`, `M x y` y `U x y` (pulsar, mover, soltar), con x e y entre 0 y 1.

## Limitaciones de esta primera versión

- La captura usa GDI (`CopyFromScreen`): sirve para validar, pero no es la más rápida ni muestra el cursor. El siguiente paso es cambiarla por Desktop Duplication (DXGI).
- Solo WiFi. Por USB haría falta usbmuxd.
- Sin icono de app.
