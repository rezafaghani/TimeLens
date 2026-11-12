FROM node:24-alpine AS build
WORKDIR /app
RUN npm install -g npm@12.0.1
COPY src/TimeLens.Client/package*.json ./
RUN npm ci
COPY src/TimeLens.Client/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_ENVSUBST_FILTER=^NGINX_LOCAL_RESOLVERS$
COPY src/TimeLens.Client/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
