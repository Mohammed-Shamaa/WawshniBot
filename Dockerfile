FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Wawshni.Bot.csproj", "./"]
RUN dotnet restore "Wawshni.Bot.csproj"
COPY . .
RUN dotnet publish "Wawshni.Bot.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV PORT=10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "Wawshni.Bot.dll"]
