# InstallerKeeper

### Mantén tu biblioteca de instaladores siempre al día

**InstallerKeeper** es una aplicación de escritorio moderna para Windows que organiza y supervisa tu colección local de archivos instaladores, consulta automáticamente las versiones oficiales más recientes mediante WinGet y GitHub Releases, y te permite descargar las actualizaciones de forma segura e intuitiva.

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](https://microsoft.com/windows)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Versión 1.1.6](https://img.shields.io/badge/versi%C3%B3n-1.1.6-087BFA?style=flat-square)](https://github.com)
[![Licencia MIT](https://img.shields.io/badge/licencia-MIT-22C55E?style=flat-square)](LICENSE)

> [!NOTE]
> InstallerKeeper administra exclusivamente **archivos instaladores**. No instala, ejecuta, actualiza ni desinstala el software residente en tu sistema operativo.

---

## ✨ Características principales

| Área | Capacidades |
|---|---|
| 📚 **Biblioteca versátil** | Soporte completo para instaladores `EXE`, `MSI`, `MSIX`, `MSIXBundle`, `APPX`, `APPXBundle` y `ZIP`. Inspección profunda de metadatos y arquitectura sin ejecutar archivos. |
| 🎛️ **Interfaz Fluent Design** | Selector de vista dual (**Tabla** de alta densidad o **Tarjetas** visuales), panel lateral **Inspector rápido** (Drawer con SHA-256, tamaño, ruta directa y acceso al Explorador) y temas Claro, Oscuro y del Sistema. |
| ⚡ **Alto rendimiento** | Comprobación en paralelo acelerada con limitación inteligente (throttling a 4 tareas simultáneas) y sistema de caché en memoria para manifiestos de WinGet. |
| 🔎 **Identificación dual** | Integración nativa con **WinGet** y **GitHub Releases**. Resolución automática de la versión más reciente con desempate configurable. |
| 🛡️ **Descargas seguras** | Descarga en aislamiento a la carpeta `%TEMP%` del sistema, a través del proxy configurado en Windows si lo hay (sin carpetas residuales en la biblioteca), comprobación estricta de hash **SHA-256**, verificación de firma digital **Authenticode** y comprobación de revocación de certificados online. |
| 🧹 **Gestor de archivo (.Archive)** | Herramienta de purga integrada con retención configurable (conservar últimas 1 o 2 versiones, o limpieza total), cálculo en tiempo real de espacio recuperable y compatibilidad total con modo oscuro. |
| 📊 **Exportación de catálogo** | Exporta la colección completa a **CSV** (con BOM para Excel), **JSON** formateado o un informe **HTML** estático y responsivo con estadísticas y badges de estado. |

---

## 🚀 Primeros pasos

1. **Abre InstallerKeeper** y selecciona la carpeta de instaladores que deseas supervisar.
2. **Configura el escaneo**: Decide si deseas incluir subdirectorios y personalizar carpetas.
3. **Identifica tus aplicaciones**: Confirma los paquetes detectados mediante los identificadores oficiales sugeridos de WinGet o GitHub Releases.
4. **Comprueba versiones**: Haz clic en **Comprobar todas** para una consulta paralela en segundo plano o comprueba aplicaciones individualmente.
5. **Descarga actualizaciones**: Descarga los paquetes oficiales directamente; InstallerKeeper validará la firma e integridad antes de promoverlos a tu biblioteca bajo la política elegida (**Reemplazar**, **Archivar en .Archive** o **Conservar todas**).

---

## 🔒 Seguridad e Integridad de Descargas

- **Aislamiento en preparación**: Los archivos se descargan en el directorio temporal seguro del sistema (`%TEMP%\InstallerKeeper\Downloads`).
- **Verificación criptográfica**: Se calcula el digest SHA-256 del binario descargado y se valida contra el hash oficial publicado.
- **Validación Authenticode**: Se verifica la cadena de certificados y revocación online. Los instaladores sin firma o con firmas inválidas quedan retenidos en preparación y no se promueven automáticamente. Los archivos firmados con catálogo, sin firma embebida, también quedan retenidos.
- **Sin carpetas intermedias**: La biblioteca permanece siempre limpia, sin carpetas auxiliares ni directorios de promoción residuales.
- **Promoción**: por defecto, InstallerKeeper pide confirmación antes de promover. Puedes activar en Opciones la promoción automática de descargas verificadas, o ocultar todos los mensajes de descarga y promoción. En ambos casos, solo se promueve lo que supera las comprobaciones de seguridad.
- **Proxy**: las descargas usan el proxy configurado en Windows. Con proxy, el proxy decide el destino final de la conexión.

---

## 📋 Requisitos del sistema

- **Sistema Operativo**: Windows 10 / Windows 11 (x64).
- **Herramientas opcionales**: Windows Package Manager (`winget`) para la consulta de paquetes WinGet (no requerido para aplicaciones configuradas exclusivamente mediante GitHub Releases).
- **Runtime**: No se requiere instalar .NET por separado (publicación autocontenida).
- **GitHub**: sin token, las consultas a GitHub Releases tienen un límite de 60 por hora.

---

## 🛠️ Compilación y Publicación

Con el SDK de .NET 10 instalado, ejecuta desde la raíz del repositorio:

```powershell
dotnet publish .\src\InstallerKeeper\InstallerKeeper.csproj --configuration Release -p:PublishProfile=WinX64 -p:PublishDir=..\..\artifacts\publish\1.1.6\win-x64\
```

El ejecutable independiente se genera en:
`artifacts/publish/1.1.6/win-x64/InstallerKeeper.exe`

Para publicar con pruebas incluidas y mostrar el SHA-256:

```powershell
.\scripts\Publish-InstallerKeeper.ps1
```


---

## 📄 Licencia

Este proyecto está bajo la licencia [MIT](LICENSE).

Se incluyen referencias y avisos de componentes de terceros:
- [Licencias de .NET para Windows](https://github.com/dotnet/core/blob/main/license-information-windows.md)
- [Licencia de Bibliotecas de .NET](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm)
- [Licencia del código fuente de WPF](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)
- [Licencia de Windows Package Manager CLI (WinGet)](https://github.com/microsoft/winget-cli/blob/master/LICENSE)
