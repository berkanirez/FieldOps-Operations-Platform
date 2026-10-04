# Day 97: multi-stage build for fieldops-web — a DIFFERENT shape from
# FieldOps.Api's (Day 58) multi-stage Dockerfile. The backend's final stage
# still RUNS a process (dotnet FieldOps.Api.dll); Angular has no process to
# run — "building" it just produces static HTML/CSS/JS files, so the final
# stage only ever needs a web server to hand those files out.

FROM node:24-alpine AS build
WORKDIR /app

# Copy only package.json/package-lock.json first — lets Docker cache the
# "npm ci" layer separately from the actual source, same reasoning as
# FieldOps.Api's Dockerfile caching "dotnet restore" before the real source.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# The much smaller final image: no Node, no npm, no source code — only the
# already-built static files plus a web server to serve them.
FROM nginx:alpine AS final
COPY --from=build /app/dist/fieldops-web/browser /usr/share/nginx/html
# Day 98: replaces nginx's own default site config (client-side routing
# fallback). Day 105: now a template — the nginx image renders
# /etc/nginx/templates/default.conf.template into /etc/nginx/conf.d/default.conf
# at container start, filling in ${API_UPSTREAM} from the environment.
COPY nginx.conf.template /etc/nginx/templates/default.conf.template

# Day 105: a deliberately harmless default so the container always starts,
# even with nothing configured: nginx resolves proxy_pass hostnames when it
# starts and refuses to start if a name can't be resolved, while "localhost"
# always resolves. With this default, /api requests simply fail with 502
# until a real address is provided (Kubernetes: the Deployment's env; Azure:
# the app's settings).
ENV API_UPSTREAM=http://localhost:5138
