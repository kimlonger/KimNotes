FROM node:22-bookworm-slim AS verify
WORKDIR /app
COPY edge-gateway/package.json ./
COPY edge-gateway/src ./src
COPY edge-gateway/tests ./tests
RUN npm test

FROM node:22-bookworm-slim AS assets
WORKDIR /site
COPY public/ ./
# Keep Cloudflare-only configuration out of the Node image, and ensure the
# update notification can only point to the package actually in this image.
RUN rm -f _headers
COPY edge-gateway/validate-release.mjs /tmp/validate-release.mjs
RUN node /tmp/validate-release.mjs /site

FROM node:22-bookworm-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production PORT=4174
COPY --from=verify /app/src ./src
COPY --from=assets /site ./public
USER node
EXPOSE 4174
CMD ["node", "src/server.mjs"]
