# WPE.Headless — mode sans interface + serveur MCP pour Claude

Ce dossier ajoute à WPE x64 :

1. **`WPE.Headless.Inject`** — DLL EasyHook injectée dans le processus cible. Installe les hooks Winsock (send/recv/sendto/recvfrom et les variantes `WSA*`), met les paquets en file et les expose sur un *named pipe* dédié au PID, **sans ouvrir de WinForms** dans la cible.
2. **`WPE.Headless.Host`** — exécutable console qui pilote l'injection, se connecte au pipe, et expose un protocole **JSON-RPC ligne par ligne** sur stdin/stdout.
3. **`mcp-server/`** — serveur MCP TypeScript qui lance `WPE.Headless.Host.exe` et expose 6 outils à Claude.

```
+--------------+   stdio JSON   +-------------------+   inject + pipe  +----------------+
|  Claude /    | <------------> | WPE.Headless.Host | <--------------> | Cible (PID N)  |
|  client MCP  |                |     (.NET 4.8)    |   (named pipe)   |  + WPE.Inject  |
+--------------+                +-------------------+                  +----------------+
        ^
        |   MCP (stdio)
        v
+--------------------+
|   wpe-headless-mcp |   (Node.js / TypeScript)
+--------------------+
```

## Outils MCP exposés

| Outil             | Description                                                                  |
|-------------------|------------------------------------------------------------------------------|
| `list_processes`  | Énumère les processus Windows (filtre optionnel sur le nom).                 |
| `inject_process`  | Injecte la DLL Winsock hook dans un PID, démarre la capture.                 |
| `get_packets`     | Récupère jusqu'à `max` paquets capturés (data en base64).                    |
| `send_packet`     | Renvoie un paquet via `send()` / `WSASend()` depuis le processus injecté.    |
| `get_stats`       | Compteurs send / recv / sendto / recvfrom / WSASend / WSARecv / queue.       |
| `stop_capture`    | Retire les hooks et ferme la session pour le PID (la cible reste lancée).    |

## Installation rapide (Windows)

Pré-requis : Visual Studio 2019/2022 ou les **Build Tools for Visual Studio** (workload .NET desktop), Node.js ≥ 18, PowerShell.

```powershell
# Depuis la racine du dépôt
pwsh -ExecutionPolicy Bypass -File .\WPE.Headless\setup.ps1
```

Le script :

1. Télécharge `nuget.exe` si nécessaire et restaure les packages
2. Compile la solution en Release (.NET 4.8) via MSBuild
3. Copie `WPE.Headless.Inject.dll` + EasyHook32/64 à côté de `WPE.Headless.Host.exe`
4. Installe et compile le serveur MCP TypeScript
5. Écrit `WPE.Headless\mcp-server\.env` avec `WPE_HOST_EXE`
6. Génère `.\.mcp.json` à la racine — détecté automatiquement par Claude Code

## Brancher dans Claude Code

### Option A — scope projet (recommandé)

Le fichier `.mcp.json` à la racine du dépôt est généré par `setup.ps1`. Lancez Claude Code dans ce dossier et il propose automatiquement d'activer `wpe-headless` :

```bash
cd C:\path\to\WinsockPacketEditor
claude
# Puis répondre 'Approve' quand Claude propose le serveur MCP wpe-headless
```

### Option B — scope utilisateur (disponible partout)

```powershell
$mcp = "C:\path\to\WinsockPacketEditor\WPE.Headless\mcp-server\dist\index.js"
$exe = "C:\path\to\WinsockPacketEditor\WPE.Headless\WPE.Headless.Host\bin\Release\WPE.Headless.Host.exe"

claude mcp add wpe-headless --scope user `
    --env WPE_HOST_EXE=$exe `
    -- node $mcp
```

### Vérifier

```bash
claude mcp list
claude mcp get wpe-headless
```

Dans une session Claude Code :

```
> /mcp
> Liste les processus contenant "notepad"
> Injecte le PID 12345
> Récupère 20 paquets capturés
```

### Brancher à Claude Desktop

Éditer `%APPDATA%\Claude\claude_desktop_config.json` :

```json
{
  "mcpServers": {
    "wpe-headless": {
      "command": "node",
      "args": ["C:\\path\\to\\WinsockPacketEditor\\WPE.Headless\\mcp-server\\dist\\index.js"],
      "env": {
        "WPE_HOST_EXE": "C:\\path\\to\\WinsockPacketEditor\\WPE.Headless\\WPE.Headless.Host\\bin\\Release\\WPE.Headless.Host.exe"
      }
    }
  }
}
```

## Compilation manuelle (si vous ne voulez pas `setup.ps1`)

```powershell
nuget restore WinSockPacketEditor.sln
msbuild WinSockPacketEditor.sln /p:Configuration=Release /p:Platform="Any CPU"

cd WPE.Headless\mcp-server
npm install
npm run build
```

> `WPE.Headless.Host.exe` doit être co-localisé avec `WPE.Headless.Inject.dll` et `EasyHook32/64.dll`. `setup.ps1` le fait automatiquement ; en build manuel, copiez ces fichiers depuis `WPELibrary\bin\Release\`.

## Test rapide en ligne de commande

Le binaire host accepte du JSON ligne par ligne sur stdin :

```text
{"id":1,"method":"list_processes","params":{"filter":"notepad"}}
{"id":2,"method":"inject_process","params":{"pid":12345}}
{"id":3,"method":"get_packets","params":{"pid":12345,"max":50}}
{"id":4,"method":"get_stats","params":{"pid":12345}}
{"id":5,"method":"send_packet","params":{"pid":12345,"socket":348,"kind":"WS2_Send","data":"R0VUIC8gSFRUUC8xLjENCg0K"}}
{"id":6,"method":"stop_capture","params":{"pid":12345}}
```

Chaque réponse est un JSON sur une ligne : `{"id":N,"result":{...}}` ou `{"id":N,"error":"..."}`.

## Brancher le serveur MCP à Claude Code

`~/.claude.json` (Windows : `C:\Users\<vous>\.claude.json`), section `mcpServers` :

```json
{
  "mcpServers": {
    "wpe-headless": {
      "command": "node",
      "args": ["C:\\path\\to\\WinsockPacketEditor\\WPE.Headless\\mcp-server\\dist\\index.js"],
      "env": {
        "WPE_HOST_EXE": "C:\\path\\to\\WinsockPacketEditor\\WPE.Headless\\WPE.Headless.Host\\bin\\Release\\WPE.Headless.Host.exe"
      }
    }
  }
}
```

Sur Claude Desktop, même structure dans le `claude_desktop_config.json`.

## Brancher à un client MCP générique

```bash
WPE_HOST_EXE="C:/path/to/WPE.Headless.Host.exe" \
  node WPE.Headless/mcp-server/dist/index.js
```

Le serveur parle MCP sur stdio.

## Limites connues (Option A — MVP)

Cette version livre les fondations :

- Pas encore d'exposition MCP des **filtres avancés**, des **robots**, du **proxy SOCKS** ni du **mode breakpoint**. Le code WPELibrary les contient mais ils restent pilotés par WinForms / SQLite. Une seconde itération (Option B) découplera ces sous-systèmes.
- Tout doit tourner avec des **droits administrateur** (EasyHook injecte du code dans un autre processus).
- Sur Windows, **Defender / SmartScreen** peut signaler `WPE.Headless.Inject.dll` à cause de `RemoteHooking.Inject`. C'est attendu pour ce type d'outil.
- L'envoi de paquets via `send_packet` utilise le **socket observé** d'un paquet capturé. Si la cible a déjà fermé ce socket, l'appel échouera.

## Avertissement

À utiliser uniquement sur des processus que vous possédez ou pour lesquels vous avez une autorisation écrite (CTF, recherche défensive, reverse engineering pédagogique). Voir le README principal.
