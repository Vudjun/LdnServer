FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
LABEL "author"="Mary <contact@mary.zone>"
WORKDIR /source

# Install NativeAOT build prerequisites
RUN apk add --no-cache clang gcc musl-dev zlib-dev

COPY *.csproj .
RUN dotnet restore -r linux-musl-arm64

COPY . .
RUN dotnet publish -c release -o /app -r linux-musl-arm64 --no-restore LanPlayServer.csproj

FROM alpine:latest
WORKDIR /app
COPY --from=build /app .

# See: https://github.com/dotnet/announcements/issues/20
ENV \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8

RUN apk add --no-cache icu-libs && addgroup -S appgroup && adduser -S appuser -G appgroup && chown -R appuser:appgroup /app
USER appuser

EXPOSE 30456
ENTRYPOINT ["./LanPlayServer"]