FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NuGet.Config Directory.Build.props global.json ./
COPY Vairas.ChatBackend.Domain/ Vairas.ChatBackend.Domain/
COPY Vairas.ChatBackend.Business/ Vairas.ChatBackend.Business/
COPY Vairas.ChatBackend.Persistance/ Vairas.ChatBackend.Persistance/
COPY Vairas.ChatBackend/ Vairas.ChatBackend/
COPY database/ database/
RUN dotnet restore Vairas.ChatBackend/Vairas.ChatBackend.csproj
RUN dotnet publish Vairas.ChatBackend/Vairas.ChatBackend.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Vairas.ChatBackend.dll"]
