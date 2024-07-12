## TAMS EF Bulk

![NuGet](https://img.shields.io/nuget/v/TAMS.EfBulk)
![NuGet](https://img.shields.io/nuget/dt/TAMS.EfBulk)
![GitHub](https://img.shields.io/github/license/TrueAnalyticsSolutions/TAMS.EfBulk)

TAMS EF Bulk provides methods for optimized bulk operations in Entity Framework using DataTables. This library aims to improve the performance of bulk insert and merge operations by leveraging DataTables and SqlBulkCopy.

## Features

- Bulk Insert
- Bulk Merge
- Support for multiple .NET frameworks:
  - .NET Core 3.1
  - .NET 4.5
  - .NET 8.0

## Getting Started

### Installation

You can install the package via NuGet Package Manager:

```shell
dotnet add package TAMS.EfBulk
```

Or via the Package Manager Console in Visual Studio:

```shell
Install-Package TAMS.EfBulk
```

### Usage

Here's a basic example of how to use the library for bulk insert and merge operations:

```csharp
using TAMS.EfBulk;
using Microsoft.EntityFrameworkCore;
using System.Data;

public class MyDbContext : DbContext
{
    // Your DbSet properties
}

public class MyEntity
{
    // Your entity properties
}

public class Example
{
    public async Task BulkOperationsAsync()
    {
        using var context = new MyDbContext();

        // Bulk Insert
        DataTable dataTable = await context.CreateDataTableAsync<MyEntity>();
        // Fill dataTable with data
        await context.BulkInsertAsync<MyEntity>(dataTable);

        // Bulk Merge
        await context.BulkMergeAsync<MyEntity>(dataTable, allowInsert: true, allowDelete: false);
    }
}
```

### Documentation

For detailed documentation, refer to the [official documentation](https://github.com/TrueAnalyticsSolutions/TAMS.EfBulk/wiki).

## Contributing

Contributions are welcome! Please read our [contributing guidelines](CONTRIBUTING.md) for more information.

## License

This project is licensed under the GNU General Public License v3.0. See the [LICENSE](LICENSE) file for details.

## Authors

- **Trais McAllister** - *Initial work* - [True Analytics Manufacturing Solutions, LLC](https://tams.ai)

## Acknowledgments

- Special thanks to the developers and contributors who have helped in creating and maintaining this project.

## Release Notes

### 0.1

- Initial release.

## Links

- [GitHub Repository](https://github.com/TrueAnalyticsSolutions/TAMS.EfBulk)
- [NuGet Package](https://www.nuget.org/packages/TAMS.EfBulk)
- [Project URL](https://github.com/TrueAnalyticsSolutions/TAMS.EfBulk)

Feel free to reach out if you have any questions or need further assistance!
