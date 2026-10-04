import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";

const dotnetUrl = process.env.LANPONG_DOTNET_URL || "http://127.0.0.1:5080";

export default defineConfig({
  root: fileURLToPath(new URL("./frontend/", import.meta.url)),
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: dotnetUrl, changeOrigin: true },
      "/ws": { target: dotnetUrl, changeOrigin: true, ws: true },
    },
  },
  build: {
    outDir: fileURLToPath(new URL("./src/LanPong/wwwroot/", import.meta.url)),
    emptyOutDir: true,
  },
});
