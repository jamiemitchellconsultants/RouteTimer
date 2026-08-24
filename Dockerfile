FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
ARG KEYCLOAK_AUTHORITY
ARG ROUTETIMER_HOSTNAME
COPY . .
RUN printf '{"Keycloak":{"authority":"%s","client_id":"routetimer-web","redirect_uri":"https://%s/authentication/login-callback","post_logout_redirect_uri":"https://%s/"}}' "$KEYCLOAK_AUTHORITY" "$ROUTETIMER_HOSTNAME" "$ROUTETIMER_HOSTNAME" > src/RouteTimer.Client/wwwroot/appsettings.Production.json
RUN dotnet restore RouteTimer.slnx
RUN dotnet publish src/RouteTimer.Api/RouteTimer.Api.csproj -c Release --no-restore -o /out/api
RUN dotnet publish src/RouteTimer.Client/RouteTimer.Client.csproj -c Release --no-restore -o /out/client

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/api .
COPY --from=build /out/client/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RouteTimer.Api.dll"]
