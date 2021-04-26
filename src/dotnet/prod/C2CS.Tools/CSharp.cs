// Copyright (c) Lucas Girouard-Stranks (https://github.com/lithiumtoast). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the Git repository root directory for full license information.

using System;

namespace C2CS.Tools
{
    public static class CSharp
    {
        public static void GenerateBindings(string arguments)
        {
            var argumentsArray =
                arguments.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Program.Main(argumentsArray);
        }
    }
}
