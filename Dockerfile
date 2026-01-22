# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/sdk:9.0
USER $APP_UID

WORKDIR /app

COPY . ./

ENV NODE_ID=B
ENV HTTP_PORT=5000
ENV DATA_DIR=/data
ENV PEERS=http://node-a:5000,http://node-c:5000

ENTRYPOINT ["dotnet", "run"]
