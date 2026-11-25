FROM mcr.microsoft.com/dotnet/aspnet:latest
WORKDIR /app
COPY ./publish .
EXPOSE 80
ENTRYPOINT ["dotnet", "Billing.Web.dll"]
