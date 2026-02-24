import { defineConfig } from "vite";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
    plugins: [tailwindcss()],
    build: {
        outDir: "wwwroot/dist",
        emptyOutDir: true,
        rollupOptions: {
            input: {
                admin: "src/css/admin.css",
                signalr: "src/js/signalr.js",
                "mvc-grid": "src/js/mvc-grid.js",
            },
            output: {
                entryFileNames: "[name].js",
                assetFileNames: "[name][extname]",
            },
        },
    },
});
