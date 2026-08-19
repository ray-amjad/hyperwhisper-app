FROM oven/bun:1 AS base
WORKDIR /app

FROM base AS deps
# bun.lock, not bun.lockb: the text lockfile has been Bun's default since 1.1.39
# and is what this repo commits. The old glob matched nothing, so the image
# installed whatever the registry served at build time — CI could pass on one
# version and prod run another. No `|| bun install` fallback for the same
# reason: a lockfile that cannot satisfy package.json must fail the build, not
# silently resolve around it.
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production

FROM base AS runner
COPY --from=deps /app/node_modules ./node_modules
COPY src ./src
COPY package.json ./

ENV NODE_ENV=production
EXPOSE 8080

CMD ["bun", "run", "src/index.ts"]
