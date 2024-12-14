# Pgrok

[![License](https://img.shields.io/github/license/pgourlain/vscode_erlang?style=for-the-badge&logo=erlang)](https://github.com/pgourlain/vscode_erlang/blob/master/LICENSE)


This project can help you debug a service hosted in an ACA (Azure Container Apps) environment.
The concept is to install a wrapper in the environment that redirects calls to your local machine (allowing you to debug the service locally)

![architecture](docs/Archi20241214.png)


# Limitations

Calling another service (in ACA) from local is not yet implemented.
