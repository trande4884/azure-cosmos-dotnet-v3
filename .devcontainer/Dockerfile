FROM mcr.microsoft.com/devcontainers/universal
FROM mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator
# RUN mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator
# COPY startemulator.sh /tmp/startemulator.sh
RUN echo "#!/bin/sh" > /tmp/startemulator.sh
RUN echo "cd /usr/local/bin/cosmos/" >> /tmp/startemulator.sh
RUN echo "nohup sh -c ./start.sh &" >> /tmp/startemulator.sh
# RUN chmod +x /workspaces/azure-cosmos-dotnet-v3/.devcontainer/startemulator.sh
# RUN ["/bin/bash", "/workspaces/azure-cosmos-dotnet-v3/.devcontainer/startemulator.sh"]
