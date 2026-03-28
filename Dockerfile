FROM node:22-bookworm-slim

WORKDIR /app

RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    python3 \
    make \
    g++ \
    pkg-config \
    libvips-dev \
  && rm -rf /var/lib/apt/lists/*

COPY package.json package-lock.json ./
RUN npm ci

COPY tsconfig.json ./
COPY tsconfig.build.json ./
COPY src ./src
COPY tests ./tests
COPY scripts ./scripts
COPY public ./public
COPY .env.example ./
COPY README.md ./

RUN npm run build
RUN npm prune --omit=dev

ENV NODE_ENV=production

RUN mkdir -p /app/data /workspace

EXPOSE 3123

CMD ["npm", "run", "start", "--", "--transport=http"]
