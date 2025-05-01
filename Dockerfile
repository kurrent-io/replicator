ARG RUNNER_IMG

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS builder
ARG TARGETARCH

WORKDIR /app

ARG RUNTIME
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g yarn 

COPY ./src/Directory.Build.props ./src/*/*.csproj ./src/
RUN for file in $(ls src/*.csproj); do mkdir -p ./${file%.*}/ && mv $file ./${file%.*}/; done
RUN dotnet restore ./src/replicator -nowarn:msb3202,nu1503 -a $TARGETARCH

COPY ./src/replicator/ClientApp/package.json ./src/replicator/ClientApp/
COPY ./src/replicator/ClientApp/yarn.lock ./src/replicator/ClientApp/
RUN cd ./src/es-replicator/ClientApp && yarn install

FROM builder AS publish
ARG TARGETARCH
COPY ./src ./src
RUN dotnet publish ./src/replicator -c Release -a $TARGETARCH -clp:NoSummary --no-self-contained -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runner

WORKDIR /app
COPY --from=publish /app/publish .

ENV ALLOWED_HOSTS "*"
ENV ASPNETCORE_URLS "http://*:5000"

EXPOSE 5000
ENTRYPOINT ["dotnet", "replicator.dll"]
