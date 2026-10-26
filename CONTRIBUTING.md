# How to contribute
So you want to write code and get it landed in the official OMRChecker repository?
First, fork our repository into your own GitHub account, and create a local clone of it as described in the installation instructions.
The latter will be used to get new features implemented or bugs fixed.

Once done and you have the code locally on the disk, you can get started. We advise you to not work directly on the master branch,
but to create a separate branch for each issue you are working on. That way you can easily switch between different work,
and you can update each one for the latest changes on the upstream master individually.


# Writing Code
This repository is the C# (.NET Framework 4.8) port of OMRChecker. Follow the conventions of the existing code
(see `AGENTS.md` if present): Simplified Chinese comments, a summary comment on every function describing
its purpose and edge cases, PascalCase for types/methods and camelCase for locals.

Also, try to use commits with [conventional messages](https://www.conventionalcommits.org/en/v1.0.0/#summary).


# Code Formatting and Tests
Before committing, format the code and make sure all tests pass:
```.sh
dotnet format OMRChecker.sln
dotnet test OMRChecker.sln
```

# Where to contribute from

- You can pickup any open [issues](https://github.com/Udayraj123/OMRChecker/issues) to solve.
- You can also check out the [ideas list](https://github.com/users/Udayraj123/projects/2/views/1)
