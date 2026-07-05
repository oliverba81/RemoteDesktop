# RemoteDesktop

Selbst gehostete Fernsteuerung für Windows — einen Windows-PC von einem anderen
aus steuern, über das Internet, ohne Port-Weiterleitung auf den Clients. Ein
kleiner Relay-Server (als **Synology-Paket**) vermittelt die Verbindung; der
gesamte Bildschirm-, Eingabe- und Dateiverkehr ist **Ende-zu-Ende verschlüsselt**
und für den Relay nicht lesbar.

> Für die Steuerung eigener bzw. mit Erlaubnis verwalteter Geräte. Der Host muss
> bewusst gestartet werden und zeigt seine Zugangsdaten nur lokal an.

## Architektur

```
 Viewer-PC (Steuerung)                Relay (Synology NAS)                Host-PC (gesteuert)
 ┌──────────────────┐   ausgehend    ┌───────────────────┐   ausgehend   ┌──────────────────┐
 │ RemoteDesktop     │ ────────────▶ │  remotedesktop-    │ ◀──────────── │ RemoteDesktop     │
 │ Viewer (WinForms) │  TCP :9945    │  relay (.spk)      │   TCP :9945   │ Host (WinForms)   │
 └──────────────────┘               └───────────────────┘               └──────────────────┘
        └───────────────  Ende-zu-Ende AES-256-GCM (Relay blind)  ───────────────┘
```

- **Beide** Endpunkte verbinden sich *ausgehend* zum Relay → kein Port-Forwarding
  am Client, funktioniert hinter üblichen Routern/NAT.
- Der Relay macht nur **Rendezvous** (Session-ID matchen) und pumpt danach Bytes
  blind weiter. Er speichert kein passwortabgeleitetes Geheimnis.

### Projekte

| Projekt | TFM | Zweck |
|---|---|---|
| `RemoteDesktop.Shared` | `net8.0` | Protokoll, Framing, Krypto, Imaging-Algorithmen (GDI-frei, unit-getestet) |
| `RemoteDesktop.Relay` | `net8.0` | Rendezvous-Relay (plattformunabhängig, läuft auf der NAS) |
| `RemoteDesktop.Host` | `net8.0-windows` | Gesteuerter PC: Bildschirmaufnahme + Eingabe-Injektion |
| `RemoteDesktop.Viewer` | `net8.0-windows` | Steuernder PC: Anzeige + Eingabe + Adressbuch |

## Sicherheitsmodell

- **Transport:** Länge-präfixierte Frames; nach dem Handshake alles
  **AES-256-GCM**. Nonce = 4-Byte-Richtungsprefix ‖ 8-Byte-Zähler (explizit pro
  Frame). Richtungsgetrennte Schlüssel via HKDF (kein Nonce-Reuse zwischen
  Richtungen). Der MessageType liegt im verschlüsselten Teil und ist so
  integritätsgeschützt.
- **Schlüsselaustausch:** ephemeres ECDH (NIST P-256) → **Forward Secrecy**. Das
  Passwort authentifiziert den Austausch über eine Argon2id-gehärtete
  Bestätigungs-MAC (HMAC über das Transkript). Ein aktiver MITM ohne Passwort
  scheitert; der Relay speichert keinen Passwort-Verifier.
- **Bekannte Einschränkung (v1):** Die Bestätigungs-MAC ist eine
  Argon2id-gehärtete Variante (EKE-artig), **kein** echtes PAKE. Ein *bösartiger*
  Relay, der Verkehr mitschneidet, hat weiterhin ein — durch Argon2id (64 MiB,
  t=3) teures — Offline-Wörterbuch-Orakel gegen schwache Passwörter. Ein echtes
  PAKE (CPace) ist die geplante v1.1-Härtung. **Nutze ein starkes Passwort.**
- **Consent:** Der Host wird manuell gestartet und zeigt pro Start frische
  Zugangsdaten. Kein unbeaufsichtigter Zugriff in v1.
- **Relay-Missbrauchsschutz:** Fehlversuch-Backoff **pro IP** (sperrt nie die
  Session — kein Aussperren des Besitzers), Query-Budget gegen Session-ID-
  Enumeration, Keepalive + Idle-Timeout gegen half-open-TCP.

## Funktionsumfang

**v1 (dieser Stand):** Bildschirmübertragung (JPEG-Kachel-Diff, adaptiv),
Maus-/Tastatursteuerung, Relay als Synology-Paket, Viewer-Adressbuch mit
Online-Anzeige, DPAPI-gespeicherte Passwörter.

**v1-Grenzen (dokumentiert):** nur **primärer Monitor**; UAC-erhöhte Fenster
empfangen keine Eingaben; `Strg+Alt+Entf` nicht injizierbar; `Win`/`Alt+Tab`
werden lokal abgefangen (noch kein Low-Level-Hook); Cursor als Position
übertragen (Form vereinfacht); Bandbreite 0,3–1,5 Mbit/s gilt für ruhenden
Desktop, bei Video/Scrollen deutlich mehr.

**Roadmap v1.1:** Reconnect-UX, Zwischenablage, Dateiübertragung (Protokoll ist
vorbereitet), unbeaufsichtigter Zugriff, echtes PAKE, DXGI-Capture, Multi-Monitor.

## Bauen & Testen

Voraussetzung: .NET 8 SDK. Der testbare, plattformunabhängige Kern (Shared +
Relay) baut und läuft überall:

```bash
dotnet build src/RemoteDesktop.Shared src/RemoteDesktop.Relay
dotnet test  tests/RemoteDesktop.Shared.Tests
```

Die WinForms-Clients (`net8.0-windows`) benötigen zum Bauen das offizielle .NET
SDK (mit Windows-Desktop-Targeting) — auf Linux via `EnableWindowsTargeting`
(bereits in `Directory.Build.props`), zur Laufzeit Windows. CI baut alles.

Windows-Clients erzeugen:

```bash
dotnet publish src/RemoteDesktop.Host   -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/RemoteDesktop.Viewer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Relay auf der Synology installieren

Voraussetzung: DSM 7, **Intel/AMD-Modell (x86_64)**.

1. **Paket bauen** (auf einem Linux/CI-Rechner mit .NET 8):
   ```bash
   bash packaging/synology/build-spk.sh 1.0.0
   # → dist/RemoteDesktopRelay-x86_64-1.0.0.spk
   ```
2. **Installieren:** Paket-Zentrum → *Einstellungen* → *Vertrauensebene* auf
   „Beliebiger Herausgeber“ (das Paket ist unsigniert) → *Manuelle Installation*
   → die `.spk` hochladen → starten. Autostart beim NAS-Boot ist enthalten.
3. **Firewall:** Systemsteuerung → Sicherheit → Firewall → den Port des Pakets
   („RemoteDesktop Relay“) erlauben.
4. **Erreichbarkeit:** im Router **einen TCP-Port** (Standard 9945) auf die NAS
   weiterleiten und per **Synology-DDNS** (z. B. `name.synology.me`) erreichbar
   machen. In restriktiven Netzen (nur 80/443 ausgehend) ggf. den Relay-Port auf
   443 legen (`var/port.conf` auf der NAS + Portweiterleitung anpassen).

Den Port ändern: Datei `/var/packages/RemoteDesktopRelay/var/port.conf` bearbeiten
und das Paket neu starten.

## Verwenden

1. **Host** (`RemoteDesktopHost.exe`) auf dem zu steuernden PC starten,
   Relay-Adresse `name.synology.me:9945` eintragen, *Start session* — er zeigt
   **Session-ID** und **Passwort**.
2. **Viewer** (`RemoteDesktopViewer.exe`) auf dem steuernden PC: Relay-Adresse,
   Session-ID und Passwort eingeben (oder im Adressbuch speichern) → *Connect*.

## Lizenz / Hinweis

Dual-Use-Software mit legitimem Zweck (eigene Geräte fernsteuern). Das Design
erschwert bewusst verdeckten Einsatz: Der Host muss manuell gestartet werden und
signalisiert den Verbindungsstatus.
