# CQRSharp

A lightweight, extensible, and attribute-driven Command Query Responsibility Segregation (CQRS) framework for .NET applications.

---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Getting Started](#getting-started)
  - [Installation](#installation)
  - [Basic Usage](#basic-usage)
- [Documentation](#documentation)
  - [Commands and Handlers](#commands-and-handlers)
  - [Attributes](#attributes)
  - [Dispatcher](#dispatcher)
- [Planned Features](#planned-features)
- [Contributing](#contributing)
- [License](#license)

---

## Overview

CQRSharp is a simple yet powerful CQRS framework designed to facilitate the implementation of the CQRS pattern in .NET applications. It emphasizes attribute-driven command handling, allowing developers to inject cross-cutting concerns (like validation, logging, and authorization) through attributes on command classes.

---

## Features

- **Attribute-Driven Pipeline**: Use attributes to define pre- and post-command execution logic.
- **Easy Integration**: Minimal setup required; integrate with your existing .NET application effortlessly.
- **Extensibility**: Create custom attributes and handlers to suit your application's needs.
- **Dependency Injection**: Leverages Microsoft's dependency injection for resolving handlers and services.
- **Asynchronous Execution**: Supports asynchronous command handling with `async/await`.

---

## Getting Started

### Installation

You can install CQRSharp via [NuGet](https://www.nuget.org/):

```shell
Install-Package CQRSharp
```

Or via the .NET CLI:

```shell
dotnet add package CQRSharp
```

### Basic Usage

#### 1. Define a Command

Create a command by implementing the `ICommand` or `ICommand<TResult>` interface:

```csharp
using CQRSharp.Interfaces.Markers;

public class CreateUserCommand : ICommand
{
    public string Username { get; set; }
    public string Email { get; set; }
}
```

#### 2. Implement a Command Handler

Implement the corresponding handler by implementing `ICommandHandler<TCommand>`:

```csharp
using CQRSharp.Interfaces.Handlers;
using System.Threading;
using System.Threading.Tasks;

public class CreateUserCommandHandler : ICommandHandler<CreateUserCommand>
{
    public Task Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        // Command handling logic here
        return Task.CompletedTask;
    }
}
```

#### 3. Use Attributes for Cross-Cutting Concerns

Apply pre- and post-command execution logic using attributes:

```csharp
using CQRSharp.Core.Pipeline;

[ValidateCommand]
[LogResult]
public class CreateUserCommand : ICommand
{
    public string Username { get; set; }
    public string Email { get; set; }
}
```

Implement the attributes:

```csharp
using CQRSharp.Core.Pipeline;
using System;
using System.Threading;
using System.Threading.Tasks;

public class ValidateCommandAttribute : Attribute, IPreCommandAttribute
{
    public Task OnBeforeHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        // Validation logic here
        return Task.CompletedTask;
    }
}

public class LogResultAttribute : Attribute, IPostCommandAttribute
{
    public Task OnAfterHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        // Logging logic here
        return Task.CompletedTask;
    }
}
```

#### 4. Configure Services

In your `Startup.cs` or wherever you configure services:

```csharp
using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;

public void ConfigureServices(IServiceCollection services)
{
    services.AddDispatcher(typeof(Startup).Assembly);

    // Register any other services
}
```

#### 5. Dispatch Commands

Use the dispatcher to send commands:

```csharp
using CQRSharp.Core.Dispatch;
using Microsoft.Extensions.DependencyInjection;

public class UserController
{
    private readonly IDispatcher _dispatcher;

    public UserController(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<IActionResult> CreateUser(CreateUserRequest request)
    {
        var command = new CreateUserCommand
        {
            Username = request.Username,
            Email = request.Email
        };

        await _dispatcher.Send(command);
        return Ok();
    }
}
```

---

## Documentation

### Commands and Handlers

- **Commands**: Represent an intent to perform an action.
- **Handlers**: Contain the logic to process commands.

Implement `ICommandHandler<TCommand>` or `ICommandHandler<TCommand, TResult>` for handling commands.

### Attributes

- **IPreCommandAttribute**: Implement to define logic before command handling.
- **IPostCommandAttribute**: Implement to define logic after command handling.

Attributes allow you to inject cross-cutting concerns without modifying the command handlers.

### Dispatcher

The `Dispatcher` is responsible for:

- Resolving the appropriate handler for a command.
- Invoking pre-handle attributes.
- Executing the command handler.
- Invoking post-handle attributes.

---

## Planned Features

- **Query Handling**: Extend support to include queries with `IQuery<TResult>` and `IQueryHandler<TQuery, TResult>`.
- **Pipeline Behaviors**: Introduce middleware-style behaviors that can wrap around command handling. This would allow global logic setting, rather than having to decorate every single class individually, with easier order control.
- **Additional order control for the pre- and post- execution attributes** via parametrization or other.
- **Enhanced Validation Integration**: Seamless integration with popular validation libraries like FluentValidation.
- **Performance Improvements**: Optimize the dispatcher and attribute invocation for better performance.
- **Configuration Options**: Provide configuration settings to customize the framework's behavior.
- **Detailed Logging and Monitoring**: Built-in support for logging frameworks and monitoring tools.

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

**Note**: This project is in active development. Stay tuned for updates and new features!

---

Please refer to the [Documentation](#documentation) section for more detailed information on using CQRSharp.
