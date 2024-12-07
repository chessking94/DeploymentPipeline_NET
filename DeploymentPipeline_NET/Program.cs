using Newtonsoft.Json;
using System.Diagnostics;
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

        static void Main(string[] args)
        {
#if DEBUG
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\.."));
            string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringDebug");
#else
            // modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.INFO, "Process started", logMethod);
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringRelease");
#endif
            string configFile = Path.Combine(projectDir, "appsettings.json");
            dynamic config = JsonConvert.DeserializeObject(File.ReadAllText(configFile))!;
            
            var projects = config["projects"];

            if (args.Length == 0)
            {
                // no argument was passed, only check to see what applications are pending deployment
                string htmlBody = "<h1><b>Projects Pending Deployment</b></h1>";  // document header
                htmlBody += "<br>";
                htmlBody += "<table>";
                htmlBody += "<tr><th>Project Name</th></tr>";  // table column header

                foreach (var entry in projects)
                {
                    var project = new Project(entry.Name, entry.Value);
                    if (project.DoDeploy)
                    {
                        htmlBody += $"<tr><td>{project.Name}</td></tr>";  // table entry
                    }
                }

                htmlBody += "</table>";

                // write and open the report
                string pendingDeploymentReport = Path.Combine(projectDir, "PendingDeployment.html");
                File.WriteAllText(pendingDeploymentReport, htmlBody);
                Process.Start(new ProcessStartInfo
                {
                    FileName = pendingDeploymentReport,
                    UseShellExecute = true // Required for opening with the default application
                });
            }
            else
            {
                // there was an argument (using "DEPLOY"), perform the deployment(s)
                if (connectionString == null)
                {
                    modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.CRITICAL, "Unable to read connection string", logMethod);
                    Environment.Exit(-1);
                }

                string projectsDeployed = "";
                foreach (var entry in projects)
                {
                    var project = new Project(entry.Name, entry.Value);
                    if (project.Deploy())
                    {
                        projectsDeployed = modStrings.AppendText(projectsDeployed, entry.Name, ", ");
                    }
                    // no need to have error handling/notifications if a deployment fails, that is handled in the AddLog call in each step
                }
#if !DEBUG
                if (!String.IsNullOrWhiteSpace(projectsDeployed))
                {
                    modNotifications.SendTelegramMessage($"A following projects have been deployed: {projectsDeployed}");
                }
                // modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.INFO, "Process ended", logMethod);
#endif   
            }
        }
    }

    /// <summary>
    /// A class structure for a project and its related methods
    /// </summary>
    internal class Project
    {
        public bool Active { get; }
        public string Name { get; }
        public string Directory { get; }
        public string Branch { get; }
        public string Language { get; }
        public string PublishDir { get; }
        public string PostDeployBatchFile { get; }
        public bool DoDeploy { get; }
        public bool DoBuild { get; }
        public string ProjectExtension { get; }
        public bool HasPostDeploy { get; }

        private const string DeployFile = "deploy.txt";

        public Project(string name, dynamic properties)
        {
            Name = name;
            Active = properties["active"];
            Directory = properties["directory"];
            Branch = properties["branch"];
            Language = properties["language"];
            PublishDir = properties["publishLocation"];
            PostDeployBatchFile = properties["postDeployBatchFile"];

            DoDeploy = CanDeploy();
            DoBuild = CanBuild();
            ProjectExtension = GetProjectExtension();
            HasPostDeploy = CanPostDeploy();
        }

        /// <summary>
        /// Perform a project deployment
        /// </summary>
        public bool Deploy()
        {
            bool deployed = false;
            // if the trigger file exists, proceed with deployment
            if (DoDeploy)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Deploying project '{Name}'", Program.logMethod);

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
                string deploymentFile = Path.Combine(Directory, DeployFile);
                File.Delete(deploymentFile);

                deployed = InstallDependencies();

                if (deployed && HasPostDeploy)
                {
                    if (!PostDeploy())
                    {
                        deployed = false;
                    }
                }

                if (deployed)
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Project '{Name}' deployment succeeded", Program.logMethod);
                }
                else
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.WARNING, $"Project '{Name}' deployment failed", Program.logMethod);
                }
            }

            return deployed;
        }

        internal bool CanDeploy()
        {
            if (Active && File.Exists(Path.Combine(Directory, DeployFile)))
            {
                return true;
            }
            return false;
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
        /// Determine if the project has a post-deploy process
        /// </summary>
        internal bool CanPostDeploy()
        {
            if (!String.IsNullOrWhiteSpace(PostDeployBatchFile) && File.Exists(PostDeployBatchFile))
            {
                return true;
            }
            return false;
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

        /// <summary>
        /// Install any dependencies for the project. Mainly used for Python.
        /// </summary>
        internal bool InstallDependencies()
        {
            switch (Language.ToUpper())
            {
                case "PYTHON":
                    if (File.Exists(Path.Combine(Directory, "requirements.txt")))
                    {
                        Int32 exitCode = modCommandLine.RunCommand($"pip install -r requirements.txt", Directory);
                        if (exitCode != 0)
                        {
                            modLogging.AddLog(Program.programName, "C#", "Project.InstallRequirements", modLogging.eLogLevel.ERROR, $"Project '{Name}' requirements.txt install failed", Program.logMethod);
                            return false;
                        }
                    }
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Execute the project post-deployment batch script
        /// </summary>
        internal bool PostDeploy()
        {
            Int32 exitCode = modCommandLine.RunCommand(PostDeployBatchFile, Directory);  // likely don't need this in the project directory, but will be consistent
            if (exitCode != 0)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.PostDeploy", modLogging.eLogLevel.ERROR, $"Post-deploy for project '{Name}' failed", Program.logMethod);
                return false;
            }
            return true;
        }
    }
}
