FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Wawshni.Bot.csproj", "./"]
RUN dotnet restore "Wawshni.Bot.csproj"
COPY . .
RUN dotnet publish "Wawshni.Bot.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/playwright/dotnet:v1.64.1-noble AS final
WORKDIR /app
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="$PATH:/usr/share/dotnet"
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
COPY --from=build /app/publish .
ENV PORT=10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "Wawshni.Bot.dll"]
