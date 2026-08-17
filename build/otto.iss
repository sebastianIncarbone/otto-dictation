; Instalador de Otto (Inno Setup 6.3+).
;
; Lo compila build\publicar.ps1, que le pasa Version y Staging. No se compila a
; mano: Staging tiene que ser la misma carpeta que ya pasó por la limpieza de
; binarios nativos ajenos y de símbolos de depuración.
;
; POR QUÉ ESTE INSTALADOR NO PIDE ADMINISTRADOR
;
; Otto es una aplicación por usuario de punta a punta: la configuración va a
; %APPDATA%, las notas y el modelo de 1,6 GB a %LOCALAPPDATA%, y el arranque
; automático a HKCU. Instalar en Archivos de programa no compartiría nada de
; eso — cada usuario igual se bajaría su propio modelo — y agregaría un aviso
; de UAC encima del de SmartScreen. Dos pantallas de miedo seguidas antes de
; ver la aplicación es peor producto, no más seriedad.
;
; DÓNDE QUEDA CADA COSA
;
;   %LOCALAPPDATA%\Programs\Otto   la aplicación (esto lo maneja el instalador)
;   %LOCALAPPDATA%\Otto            notas, modelos
;   %APPDATA%\Otto                 configuración
;
; Son carpetas hermanas y separadas a propósito: el desinstalador borra la
; primera sin tocar las otras dos, así reinstalar no te cuesta el historial ni
; otra descarga de 1,6 GB.

#ifndef Version
  #error Falta /DVersion. Compilalo con build\publicar.ps1.
#endif

#ifndef Staging
  #error Falta /DStaging. Compilalo con build\publicar.ps1.
#endif

; VersionInfoVersion sólo acepta números. Version puede traer un sufijo de
; prelanzamiento, así que publicar.ps1 manda la forma limpia por separado.
#ifndef NumericVersion
  #define NumericVersion Version
#endif

#define AppName "Otto"
#define Publisher "Sebastian Incarbone"
#define RepoUrl "https://github.com/sebastianIncarbone/otto-dictation"
#define Exe "Otto.App.exe"

[Setup]
AppId={{08FD4B32-9406-4142-A528-1E908B2A4A09}
AppName={#AppName}
AppVersion={#Version}
VersionInfoVersion={#NumericVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases

; {autopf} con PrivilegesRequired=lowest resuelve a %LOCALAPPDATA%\Programs,
; que es donde instalan las aplicaciones por usuario en Windows moderno.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest

; El grupo del menú Inicio no se elige: preguntarle a alguien cómo quiere que
; se llame una carpeta es una pantalla que nadie leyó nunca. Que el acceso
; directo exista o no sí se elige, abajo en [Tasks].
DisableProgramGroupPage=yes

LicenseFile={#SourcePath}..\LICENSE
OutputDir={#SourcePath}..\dist
OutputBaseFilename=Otto-Setup
SetupIconFile={#SourcePath}..\src\Otto.App\Otto.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#AppName}

WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; Otto solo existe para Windows x64: los binarios nativos de whisper.cpp que
; viajan adentro son win-x64 y nada más.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Otto vive en la bandeja, así que actualizar encima de una versión corriendo
; es el caso normal, no el raro. Sin esto los archivos quedan bloqueados y la
; instalación falla a la mitad.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startmenuicon"; Description: "Crear un acceso directo en el menú Inicio"; GroupDescription: "Accesos directos:"
Name: "desktopicon";   Description: "Crear un acceso directo en el escritorio";  GroupDescription: "Accesos directos:"

; El arranque con Windows NO se ofrece acá a propósito. Ya es una opción dentro
; de Otto, que escribe la clave Run y refleja el estado real; ponerlo también
; en el instalador crearía dos fuentes de verdad que se pisan la primera vez
; que alguien toque Guardar en la configuración.

[Files]
Source: "{#Staging}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\{#Exe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#Exe}"; Tasks: desktopicon

[Registry]
; No se crea acá — la escribe Otto cuando marcás "iniciar con Windows" — pero
; sí se borra al desinstalar. Una entrada Run apuntando a un ejecutable que ya
; no existe falla en silencio en cada arranque de Windows, para siempre.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Otto"; \
    Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#Exe}"; Description: "Ejecutar Otto"; Flags: nowait postinstall skipifsilent

[Code]

function DataDirs(): TArrayOfString;
begin
  SetArrayLength(Result, 2);
  Result[0] := ExpandConstant('{userappdata}\Otto');
  Result[1] := ExpandConstant('{localappdata}\Otto');
end;

function AnyDataExists(): Boolean;
var
  Dirs: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Dirs := DataDirs();

  for I := 0 to GetArrayLength(Dirs) - 1 do
    if DirExists(Dirs[I]) then
    begin
      Result := True;
      Exit;
    end;
end;

procedure DeleteData();
var
  Dirs: TArrayOfString;
  I: Integer;
begin
  Dirs := DataDirs();

  for I := 0 to GetArrayLength(Dirs) - 1 do
    DelTree(Dirs[I], True, True, True);
end;

function WantsDataDeleted(): Boolean;
var
  I: Integer;
begin
  Result := False;

  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/DELETEDATA') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ Los datos del usuario no se borran solos. Las notas son lo único irrecuperable
  que produce Otto, y el modelo son 1,6 GB que nadie quiere volver a bajar
  porque desinstaló para probar otra versión. Se pregunta, y la respuesta por
  defecto es conservar. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if not AnyDataExists() then
    Exit;

  { En modo silencioso NO se pregunta, y por una razón que muerde: Inno responde
    que sí a los cuadros de Sí/No cuando se los suprime con /SUPPRESSMSGBOXES.
    Es decir que la versión "cuidadosa" de este código — preguntar siempre —
    borraría las notas y 1,6 GB de modelo, sin pantalla y sin que nadie lo haya
    pedido, en cuanto alguien desinstale desde un script o una herramienta de
    administración. Conservar es lo único seguro cuando no hay nadie mirando.
    Quien de verdad quiera limpiar todo, lo dice con /DELETEDATA. }
  if WizardSilent() then
  begin
    if WantsDataDeleted() then DeleteData();
    Exit;
  end;

  if MsgBox('¿Querés borrar también tus notas, la configuración y los modelos de voz descargados?'
            + #13#10#13#10
            + 'Si elegís No, todo eso queda en tu disco y lo vas a recuperar tal cual si volvés a instalar Otto.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DeleteData();
end;
