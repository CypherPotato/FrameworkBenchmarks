FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source

COPY src/ src/
COPY sisk/*.csproj sisk/
RUN dotnet restore sisk/sisk.csproj -r linux-musl-x64

COPY sisk/ sisk/
RUN dotnet publish sisk/sisk.csproj -c release -o /app -r linux-musl-x64 --no-restore --self-contained

# final stage/image
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine

ENV DOTNET_GCDynamicAdaptationMode=0
ENV DOTNET_ReadyToRun=0
ENV DOTNET_HillClimbing_Disable=1
ENV DB_CONNECTION="Server=tfb-database;Database=hello_world;User Id=benchmarkdbuser;Password=benchmarkdbpass;SSL Mode=Disable;Maximum Pool Size=18;Enlist=false;Max Auto Prepare=4;Multiplexing=true;Write Coalescing Buffer Threshold Bytes=1000;"

WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["./sisk"]

EXPOSE 8080
