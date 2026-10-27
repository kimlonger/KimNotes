FROM golang:1.26.3-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY *.go ./
COPY migrations ./migrations
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /kimcloud .
FROM alpine:3.23
RUN apk add --no-cache ca-certificates && adduser -D -u 10001 app
USER app
COPY --from=build /kimcloud /usr/local/bin/kimcloud
ENV LISTEN_ADDR=:8080
EXPOSE 8080
CMD ["kimcloud"]
