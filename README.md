# CQRSharp
[![NuGet version (CQRSharp)](https://img.shields.io/nuget/v/CQRSharp.svg?style=flat-square)](https://www.nuget.org/packages/CQRSharp/)
[![CodeQL](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql/badge.svg?branch=Release)](https://github.com/BisocM/CQRSharp/actions/workflows/github-code-scanning/codeql)

A lightweight, extensible, and attribute-driven Command Query Responsibility Segregation (CQRS) framework for .NET applications.

For more information, please check out the [wiki](https://github.com/BisocM/CQRSharp/wiki) page!

---

## Planned Features

- **Additional order control for the pre- and post-execution attributes** via parametrization or other.
- **Enhanced Validation Integration**: Seamless integration with popular validation libraries like FluentValidation.
- **Performance Improvements**: Optimize the dispatcher and attribute invocation for better performance.
- **Event Publishing**: Event publishing mechanisms via RabbitMQ or Kafka for domain event handling.
- **Transactional Behavior**: To support atomicity during command execution.
- **Caching**: Command idempotency, distributed caching systems & basic in-memory caching.
- **Bulkhead Isolation**
- **Dead Letter Queue**
- **Tenant-Aware Commands**
- **Compensatory Mechanisms + Saga Support**: Implement compensatory mechanisms for long-running commands.

---

## Contributing

Contributions are welcome! Please open issues and pull requests for bug fixes, enhancements, or new features.

To contribute:

1. Fork the repository.
2. Create a new branch.
3. Make your changes.
4. Submit a pull request.

Please ensure that your code follows the project's coding standards and includes appropriate tests.

---

## License

This project is licensed under the [MIT License](LICENSE).

---

**Note**: This project is in active development.
