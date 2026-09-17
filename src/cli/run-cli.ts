import { initializeConfig, loadRuntimeConfig } from "../config/load-config.js";
import { ApplicationLifecycle } from "../app/lifecycle.js";
import { ChatRoomError } from "../core/errors/chatroom-error.js";

export async function runChatRoomCli(
  argv: string[] = process.argv.slice(2),
): Promise<void> {
  const [command = "help"] = argv;
  if (command === "init") return init();
  if (command === "serve") return serve();
  if (command === "auth" && argv[1] === "token") return printOwnerToken();
  printHelp();
}

async function init(): Promise<void> {
  const result = await initializeConfig();
  console.log(`ChatRoom initialized: ${result.configPath}`);
  console.log(`Allowed roots: ${result.config.allowedRoots.join(", ")}`);
  console.log("Owner token stored securely in the system keychain.");
  console.log(
    "Local loopback WebUI does not require authentication by default. Use `chatroom auth token` only when you explicitly need to reveal the owner token.",
  );
}

async function serve(): Promise<void> {
  const runtime = await loadRuntimeConfig();
  const { config, ownerToken } = runtime;
  const lifecycle = new ApplicationLifecycle(config, { ownerToken });
  await lifecycle.start();
  console.log(
    `ChatRoom listening on http://${config.server.host}:${config.server.port}`,
  );
  let closing = false;
  const shutdown = () => {
    if (closing) return;
    closing = true;
    void lifecycle
      .shutdown()
      .then(() => process.exit(0))
      .catch((error) => {
        console.error(error);
        process.exit(1);
      });
  };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
}

async function printOwnerToken(): Promise<void> {
  // Loading the runtime also performs the one-time migration from a legacy
  // plaintext auth.ownerToken before revealing the secret on explicit request.
  const { ownerToken } = await loadRuntimeConfig();
  if (!ownerToken)
    throw new ChatRoomError("NOT_FOUND", "No owner token is stored for ChatRoom");
  console.log(ownerToken);
}

function printHelp(): void {
  console.log(
    "ChatRoom\n\nUsage:\n  chatroom init\n  chatroom serve\n  chatroom auth token",
  );
}
