# Podium web/API image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Podium.Core/Podium.Core.csproj src/Podium.Core/
COPY src/Podium.Web/Podium.Web.csproj src/Podium.Web/
RUN dotnet restore src/Podium.Web/Podium.Web.csproj
COPY src/ src/
RUN dotnet publish src/Podium.Web/Podium.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=false \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Podium.Web.dll"]
