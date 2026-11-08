# GameHelper2 für Windows x64

## Voraussetzungen

- Windows x64 mit Direct3D 11 und ein installierter Path-of-Exile-2-Client.
- [.NET Desktop Runtime 10.0 für Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
  Sie enthält die .NET Runtime. Mit `dotnet --list-runtimes` kannst du die
  Installation prüfen. Zum Starten genügt die Runtime.

Das ZIP enthält Launcher, Overlay, Fonts, Übersetzungen und die neun Plugins
aus der Solution. Die benötigten Bibliotheken sind enthalten, darunter
ImGui.NET, ClickableTransparentOverlay, Vortice, ImageSharp, Newtonsoft.Json,
Dynamic LINQ und AsmResolver. NuGet-Pakete müssen für die Nutzung des ZIPs
nicht separat installiert werden.

## Installation

1. Entpacke den gesamten Ordner `GameHelper` an einen Ort, an dem du
   Dateien schreiben darfst.
2. Starte Path of Exile 2.
3. Starte `Launcher.exe` im entpackten Ordner. Läuft das Spiel erhöht,
   starte den Launcher ebenfalls als Administrator.
4. Aktiviere in den GameHelper-Einstellungen die benötigten Plugins.
   Für Atlas2 öffne anschließend den Endgame-Atlas im Spiel.

Deine Profile und Einstellungen werden lokal unter `configs` und
`Plugins/<Plugin>/config` gespeichert. Wenn du von einer früheren
Installation wechselst, sichere zuerst diese Ordner. Übernimm die Einstellungen
separat in die neue Installation. Zusatzplugins aus anderen Quellen gehören
nicht zum Paket.

Die Änderungen des Builds stehen in `CHANGELOG.md`. Welche PoE2-Version
unterstützt wird, hängt vom Stand der Offsets ab.
