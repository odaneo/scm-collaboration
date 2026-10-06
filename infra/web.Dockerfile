FROM node:22.20.0-bookworm-slim@sha256:b21fe589dfbe5cc39365d0544b9be3f1f33f55f3c86c87a76ff65a02f8f5848e
WORKDIR /app
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build
RUN chown -R node:node /app
USER node
# ponytail: 本地教学使用 Vite dev server；正式部署阶段改为静态文件托管。
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]
