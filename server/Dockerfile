# Shared office MCP server: employees connect over HTTP, each request is
# routed to the Revit plugin on that employee's PC (see users.example.json).
FROM node:22-bookworm-slim AS build
# better-sqlite3 falls back to compiling when no prebuilt binary matches
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json ./
COPY src ./src
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim
ENV NODE_ENV=production \
    MCP_TRANSPORT=http \
    MCP_HTTP_PORT=3000 \
    REVIT_MCP_AUTH=none \
    REVIT_MCP_USERS_FILE=/config/users.json \
    REVIT_MCP_DB_PATH=/data/revit-data.db \
    REVIT_MCP_RELEASES_DIR=/releases
WORKDIR /app
COPY --from=build /app/package.json ./
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/build ./build
RUN mkdir -p /data /config /releases && chown node:node /data
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s \
    CMD node -e "fetch('http://localhost:'+process.env.MCP_HTTP_PORT+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "build/index.js"]
