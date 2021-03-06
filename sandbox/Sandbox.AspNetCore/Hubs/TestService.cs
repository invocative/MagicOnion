using MagicOnion;
using System;
using System.Collections.Generic;
using System.Text;

namespace Sandbox.AspNetCore.Hubs
{
    public interface ITestService : IService<ITestService>
    {
        UnaryResult<long[]> FooBarBaz(string x, long y);
    }
}
