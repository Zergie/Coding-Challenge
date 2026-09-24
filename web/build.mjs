import { build } from "esbuild";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";

const target = process.env.WEB_TARGET === "local" ? "local" : "azure";
await mkdir("dist", { recursive: true });
await Promise.all([
  copyFile("public/index.html", "dist/index.html"),
  copyFile("public/auth.html", "dist/auth.html"),
  copyFile("public/style.css", "dist/style.css"),
  copyFile(`config.${target}.json`, "dist/config.json"),
  copyFile("public/staticwebapp.config.json", "dist/staticwebapp.config.json")
]);
await build({ entryPoints: ["src/app.ts", "src/redirect.ts"], outdir: "dist", bundle: true,
  format: "esm", target: "es2022", minify: true, sourcemap: true });
const config = JSON.parse(await readFile("dist/config.json", "utf8"));
if (!config.tenantId || !config.clientId || !config.scope || config.apiBaseUrl === undefined) {
  throw new Error("Web configuration is incomplete.");
}
await writeFile("dist/config.json", JSON.stringify(config));
