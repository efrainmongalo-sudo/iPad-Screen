; Instalador de "Monitor secundario" (Inno Setup 6)
#define AppName "Monitor secundario"
#define AppVersion "1.2.0"
#define AppExe "MonitorServidor.exe"

[Setup]
AppId={{B6E2F0A4-5D1C-4B7E-9A3F-2C8D41E7A5B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Netnologia
DefaultDirName={autopf}\MonitorSecundario
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\..\installer-out
OutputBaseFilename=MonitorSecundario-Setup
SetupIconFile=..\MonitorServidor\app.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Iniciar con Windows (se abre junto al reloj y empieza a transmitir)"; GroupDescription: "Inicio automático:"; Flags: unchecked

[Files]
Source: "..\..\out\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--minimizado"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir Monitor secundario"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// El programa habla con la iPad por USB a través del servicio que instala iTunes.
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service') then
    MsgBox('No se encontró «Apple Mobile Device Service», que viene con iTunes.' + #13#10 + #13#10 +
           'El programa lo necesita para comunicarse con la iPad por el cable USB. ' +
           'Instala iTunes desde apple.com (no la versión de Microsoft Store) antes de usarlo.',
           mbInformation, MB_OK);
end;

// Después de copiar los archivos, instala la pantalla virtual (driver) para que todo quede listo antes de abrir el programa.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Instalando la pantalla virtual (puede tardar un minuto)...';
    WizardForm.FileNameLabel.Caption := '';
    if (not Exec(ExpandConstant('{app}\{#AppExe}'), '--instalar-driver', '', SW_HIDE, ewWaitUntilTerminated, ResultCode))
       or (ResultCode <> 0) then
      MsgBox('No se pudo instalar la pantalla virtual automáticamente.' + #13#10 + #13#10 +
             'El programa volverá a intentarlo la primera vez que inicies la transmisión. ' +
             'Si tampoco funciona, comprueba tu conexión a internet y vuelve a ejecutar este instalador.',
             mbInformation, MB_OK);
  end;
end;
