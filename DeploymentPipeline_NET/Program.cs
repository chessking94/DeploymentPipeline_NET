﻿using Newtonsoft.Json;
using System.Reflection;
using Utilities_NetCore;

namespace DeploymentPipeline
{
    internal class Program
    {
        public static string programName = Assembly.GetExecutingAssembly().GetName().Name!;
#if DEBUG
        public const modLogging.eLogMethod logMethod = modLogging.eLogMethod.CONSOLE;
#else
        public const modLogging.eLogMethod logMethod = modLogging.eLogMethod.DATABASE;
#endif

        static void Main()
        {
#if DEBUG
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\.."));
            string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringDebug");
#else
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringRelease");
#endif
            string configFile = Path.Combine(projectDir, "appsettings.json");
            dynamic config = JsonConvert.DeserializeObject(File.ReadAllText(configFile))!;

            if (connectionString == null)
            {
                modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.CRITICAL, "Unable to read connection string", logMethod);
                Environment.Exit(-1);
            }

            var projects = config["projects"];
            foreach (var entry in projects)
            {
                var project = new Project(entry.Name, entry.Value);
                project.Deploy();
            }
        }
    }

    /// <summary>
    /// A class structure for a project and its related methods
    /// </summary>
    internal class Project
    {
        public string Name { get; }
        public string Directory { get; }
        public string Branch { get; }
        public string Language { get; }
        public string PublishDir { get; }
        public bool DoBuild { get; }
        public string ProjectExtension { get; }
        private const string DeployFile = "deploy.txt";

        public Project(string name, dynamic properties)
        {
            Name = name;
            Directory = properties["directory"];
            Branch = properties["branch"];
            Language = properties["language"];
            PublishDir = properties["publishLocation"];
            DoBuild = CanBuild();
            ProjectExtension = GetProjectExtension();
        }

        /// <summary>
        /// Perform a project deployment
        /// </summary>
        public void Deploy()
        {
            // if the trigger file exists, proceed with deployment
            string deploymentFile = Path.Combine(Directory, DeployFile);
            if (File.Exists(deploymentFile))
            {
                modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Deploying project '{Name}'", Program.logMethod);

                bool deployed = false;
                if (Pull())
                {
                    if (!DoBuild)
                    {
                        deployed = true;
                    }
                    else
                    {
                        if (Build())
                        {
                            if (Publish())
                            {
                                deployed = true;
                            }
                        }
                    }
                }

                // remove the deployment trigger file
                File.Delete(deploymentFile);

                if (deployed)
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Project '{Name}' deployment succeeded", Program.logMethod);
                }
                else
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.WARNING, $"Project '{Name}' deployment failed", Program.logMethod);
                }
            }
        }

        /// <summary>
        /// Determine if the project language supports builds
        /// </summary>
        internal bool CanBuild()
        {
            bool canBuild = false;
            switch (Language.ToUpper())
            {
                case "PYTHON":
                    canBuild = false;
                    break;
                case "VB":
                case "C#":
                    canBuild = true;
                    break;
                default:
                    canBuild = false;
                    break;
            }

            if (canBuild)
            {
                if (!String.IsNullOrWhiteSpace(PublishDir) && !System.IO.Directory.Exists(PublishDir))
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.CanBuild", modLogging.eLogLevel.ERROR, $"Project '{Name}' has an invalid publish directory '{PublishDir}'", Program.logMethod);
                    canBuild = false;
                }
            }

            return canBuild;
        }

        /// <returns>
        /// Visual Studio project extension for the language
        /// </returns>
        internal string GetProjectExtension()
        {
            switch (Language.ToUpper())
            {
                case "PYTHON":
                    return "pyproj";
                case "VB":
                    return "vbproj";
                case "C#":
                    return "csproj";
                default:
                    modLogging.AddLog(Program.programName, "C#", "Project.GetProjectExtension", modLogging.eLogLevel.CRITICAL, $"Project language {Language} not supported", Program.logMethod);
                    Environment.Exit(-1);
                    break;
            }
            return "";
        }

        /// <summary>
        /// Pull the specified Git branch
        /// </summary>
        internal bool Pull()
        {
            Int32 exitCode = modCommandLine.RunCommand($"git pull origin {Branch}", Directory);
            if (exitCode != 0)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.Pull", modLogging.eLogLevel.ERROR, $"Git pull for project '{Name}' failed", Program.logMethod);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Build a project, currently only supports .NET
        /// </summary>
        internal bool Build()
        {
            Int32 exitCode = modCommandLine.RunCommand("dotnet build -c Release", Directory);
            if (exitCode != 0)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.Build", modLogging.eLogLevel.ERROR, $"Project '{Name}' build failed", Program.logMethod);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Publish a project, currently only supports .NET
        /// </summary>
        internal bool Publish()
        {
            if (!String.IsNullOrWhiteSpace(PublishDir))
            {
                Int32 exitCode = modCommandLine.RunCommand($"dotnet publish {Name}.{ProjectExtension} -c Release --no-build -o \"{PublishDir}\"", Directory);
                if (exitCode != 0)
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Publish", modLogging.eLogLevel.ERROR, $"Project '{Name}' build failed", Program.logMethod);
                    return false;
                }
            }
            return true;
        }
    }
}
