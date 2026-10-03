# Build the SPA, then serve the static output from nginx.
#
# Node is only needed to produce the bundle; the result is static files, so the runtime image is
# nginx rather than Node. That removes a JavaScript runtime from the attack surface entirely.

FROM node:20-alpine AS build
WORKDIR /app

# Manifests first so the dependency install layer is cached independently of source changes.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build -- --configuration production

FROM nginx:1.27-alpine AS final

# The default config does not know about client-side routing: a deep link such as /devices/12 is
# not a file on disk, and nginx would answer 404.
COPY nginx.conf /etc/nginx/conf.d/default.conf

COPY --from=build /app/dist/devicepulse-web/browser /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1/ > /dev/null || exit 1"]
