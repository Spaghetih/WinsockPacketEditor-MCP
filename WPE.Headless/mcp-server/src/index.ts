#!/usr/bin/env node
/**
 * Serveur MCP pour WPE x64 headless.
 *
 * Lance le binaire WPE.Headless.Host.exe en sous-processus, communique en
 * JSON-RPC ligne par ligne sur stdio, et expose 6 outils MCP à Claude :
 *   - list_processes    : énumère les processus Windows (filtre optionnel)
 *   - inject_process    : injecte la DLL Winsock hook dans un PID
 *   - get_packets       : récupère les paquets capturés depuis l'injection
 *   - send_packet       : envoie un paquet via le socket d'un PID injecté
 *   - get_stats         : récupère les compteurs send/recv du PID
 *   - stop_capture      : retire les hooks et ferme la session
 */
import { spawn, ChildProcessWithoutNullStreams } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import * as readline from "node:readline";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// ---------- localiser WPE.Headless.Host.exe ----------

function loadDotEnv() {
  const envPath = path.resolve(__dirname, "../.env");
  if (!existsSync(envPath)) return;
  for (const line of readFileSync(envPath, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*?)\s*$/i);
    if (!m) continue;
    const [, k, raw] = m;
    if (process.env[k]) continue;
    process.env[k] = raw.replace(/^["']|["']$/g, "");
  }
}

function resolveHostExe(): string {
  loadDotEnv();
  if (process.env.WPE_HOST_EXE && existsSync(process.env.WPE_HOST_EXE)) {
    return process.env.WPE_HOST_EXE;
  }
  const candidates = [
    path.resolve(__dirname, "../../WPE.Headless.Host/bin/Release/WPE.Headless.Host.exe"),
    path.resolve(__dirname, "../../WPE.Headless.Host/bin/Debug/WPE.Headless.Host.exe"),
  ];
  for (const c of candidates) if (existsSync(c)) return c;
  throw new Error(
    "WPE.Headless.Host.exe introuvable. Lancez WPE.Headless\\setup.ps1 pour compiler la solution, " +
      "ou fournissez le chemin via la variable d'environnement WPE_HOST_EXE " +
      "(ou via WPE.Headless/mcp-server/.env)."
  );
}

// ---------- pont JSON-RPC vers le host ----------

type PendingResolver = {
  resolve: (v: unknown) => void;
  reject: (e: Error) => void;
};

class HostBridge {
  private proc: ChildProcessWithoutNullStreams | null = null;
  private pending = new Map<number, PendingResolver>();
  private nextId = 1;

  constructor() {}

  /** Lazy spawn so `tools/list` works even before the .NET host is built. */
  private ensure() {
    if (this.proc) return;
    const exePath = resolveHostExe();
    this.proc = spawn(exePath, [], { stdio: ["pipe", "pipe", "pipe"] });
    this.proc.on("error", (err) => process.stderr.write(`[wpe-host] spawn error: ${err}\n`));
    this.proc.stderr.on("data", (b) => process.stderr.write(`[wpe-host] ${b}`));

    const rl = readline.createInterface({ input: this.proc.stdout });
    rl.on("line", (line) => this.onLine(line));

    this.proc.on("exit", (code) => {
      this.proc = null;
      for (const [, p] of this.pending) p.reject(new Error(`host exited (code=${code})`));
      this.pending.clear();
    });
  }

  private onLine(line: string) {
    let msg: any;
    try {
      msg = JSON.parse(line);
    } catch {
      return;
    }
    if (msg.id !== undefined) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new Error(String(msg.error)));
      else p.resolve(msg.result);
    } else if (msg.type === "notification") {
      process.stderr.write(`[wpe-host:${msg.channel}] ${JSON.stringify(msg.data)}\n`);
    }
  }

  call(method: string, params: Record<string, unknown> = {}): Promise<unknown> {
    this.ensure();
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      this.pending.set(id, { resolve, reject });
      const payload = JSON.stringify({ id, method, params }) + "\n";
      this.proc!.stdin.write(payload, (err) => {
        if (err) {
          this.pending.delete(id);
          reject(err);
        }
      });
      setTimeout(() => {
        if (this.pending.has(id)) {
          this.pending.delete(id);
          reject(new Error(`timeout waiting for ${method}`));
        }
      }, 30_000);
    });
  }

  shutdown() {
    try {
      this.proc?.kill();
    } catch {
      /* ignore */
    }
  }
}

// ---------- définition des outils MCP ----------

const TOOLS = [
  {
    name: "list_processes",
    description:
      "Liste les processus Windows en cours d'exécution. Utile pour trouver le PID d'un programme à injecter. Filtre optionnel sur le nom.",
    inputSchema: {
      type: "object",
      properties: {
        filter: { type: "string", description: "Sous-chaîne casse-insensible filtrant les noms de processus." },
      },
    },
  },
  {
    name: "inject_process",
    description:
      "Injecte la DLL Winsock hook dans un PID cible. Démarre la capture des paquets WS1.1 / WS2.0 (send, recv, sendto, recvfrom, WSASend, WSARecv).",
    inputSchema: {
      type: "object",
      properties: {
        pid: { type: "integer", description: "PID du processus cible." },
      },
      required: ["pid"],
    },
  },
  {
    name: "get_packets",
    description:
      "Récupère jusqu'à `max` paquets capturés depuis le dernier appel pour un PID injecté. Le champ `data` est en base64.",
    inputSchema: {
      type: "object",
      properties: {
        pid: { type: "integer" },
        max: { type: "integer", default: 100, minimum: 1, maximum: 5000 },
      },
      required: ["pid"],
    },
  },
  {
    name: "send_packet",
    description:
      "Envoie un paquet via send()/WSASend() depuis le processus injecté en utilisant un socket actif observé par get_packets.",
    inputSchema: {
      type: "object",
      properties: {
        pid: { type: "integer" },
        socket: { type: "integer", description: "Handle de socket observé (champ `socket` d'un paquet capturé)." },
        kind: {
          type: "string",
          enum: ["WS1_Send", "WS2_Send"],
          default: "WS2_Send",
          description: "Variante d'API à utiliser.",
        },
        data: { type: "string", description: "Charge utile du paquet, encodée en base64." },
      },
      required: ["pid", "socket", "data"],
    },
  },
  {
    name: "get_stats",
    description: "Compteurs de paquets côté processus injecté (send, recv, sendto, recvfrom, wsasend, wsarecv, queued).",
    inputSchema: {
      type: "object",
      properties: { pid: { type: "integer" } },
      required: ["pid"],
    },
  },
  {
    name: "stop_capture",
    description: "Stoppe la capture et ferme la session pour le PID. Le processus cible continue de tourner.",
    inputSchema: {
      type: "object",
      properties: { pid: { type: "integer" } },
      required: ["pid"],
    },
  },
] as const;

// ---------- assemblage MCP ----------

async function main() {
  const bridge = new HostBridge();

  const server = new Server(
    { name: "wpe-headless", version: "1.0.0" },
    { capabilities: { tools: {} } }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS as any }));

  server.setRequestHandler(CallToolRequestSchema, async (req) => {
    const { name, arguments: args } = req.params;
    const params = (args ?? {}) as Record<string, unknown>;
    try {
      const result = await bridge.call(name, params);
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
      };
    } catch (err) {
      return {
        isError: true,
        content: [{ type: "text", text: `Erreur: ${(err as Error).message}` }],
      };
    }
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);

  const cleanup = () => {
    bridge.shutdown();
    process.exit(0);
  };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main().catch((err) => {
  process.stderr.write(`[wpe-mcp] fatal: ${err}\n`);
  process.exit(1);
});
