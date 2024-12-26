using Microsoft.Data.SqlClient;
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
        public static readonly string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\.."));
        public static readonly string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringDebug");
#else
        public const modLogging.eLogMethod logMethod = modLogging.eLogMethod.DATABASE;
        public static readonly string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        public static readonly string? connectionString = Environment.GetEnvironmentVariable("ConnectionStringRelease");
#endif

        static void Main(string[] args)
        {
            try
            {
                if (connectionString == null)
                {
                    modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.CRITICAL, "Unable to read connection string", logMethod);
                    Environment.Exit(-1);
                }

                var projects = new List<Project>();

                var command = new SqlCommand();
                command.Connection = modDatabase.Connection(connectionString);
                command.CommandType = System.Data.CommandType.Text;
                command.CommandText = @"
SELECT
RepoName,
ProjectFilePath,
GitBranch,
PublishPath,
PostDeployBatchFile

FROM HuntHome.dev.Repositories

WHERE AutomatedDeployment = 1
AND DeploymentQueued = 1

ORDER BY DeploymentGroup, RepoName
";

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var project = new Project(
                            name: reader["RepoName"].ToString()!,
                            projectfilepath: reader["ProjectFilePath"].ToString()!,
                            branch: reader["GitBranch"].ToString()!,
                            publishdir: reader["PublishPath"] != DBNull.Value ? reader["PublishPath"].ToString()! : string.Empty,
                            postdeploybatchfile: reader["PostDeployBatchFile"] != DBNull.Value ? reader["PostDeployBatchFile"].ToString()! : string.Empty
                        );

                        projects.Add(project);
                    }
                }
                command.Dispose();

                if (args.Length == 0)
                {
                    // no argument was passed, only check to see what applications are pending deployment
                    string htmlBody = "<h1><b>Projects Pending Deployment</b></h1>";  // document header
                    htmlBody += "<br>";
                    htmlBody += "<table>";
                    htmlBody += "<tr><th>Project Name</th></tr>";  // table column header

                    foreach (var project in projects)
                    {
                        htmlBody += $"<tr><td>{project.Name}</td></tr>";  // table entry
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
                else if (args.Length == 1 && args[0].ToUpper() == "DEPLOY")
                {
                    // there was an argument (using "DEPLOY"), perform the deployment(s)
                    string projectsDeployed = "";
                    foreach (var project in projects)
                    {
                        if (project.Deploy())
                        {
                            projectsDeployed = modStrings.AppendText(projectsDeployed, project.Name, ", ");
                        }
                        // no need to have error handling/notifications if a deployment fails, that is handled in the AddLog call in each step
                    }
#if !DEBUG
                    if (!String.IsNullOrWhiteSpace(projectsDeployed))
                    {
                        modNotifications.SendTelegramMessage($"The following project(s) have been deployed: {projectsDeployed}");
                    }
#endif
                }
                else
                {
                    // something unexpected occurred, log as such
                    modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.CRITICAL, $"Invalid arguments passed! Count = {args.Length}, First = {args[0]}", logMethod);
                }
            }
            catch (Exception ex)
            {
                modLogging.AddLog(programName, "C#", "Program.Main", modLogging.eLogLevel.CRITICAL, $"Catastrohic error! {ex.Message} | {ex.StackTrace}", logMethod);
            }
        }
    }

    /// <summary>
    /// A class structure for a project and its related methods
    /// </summary>
    internal class Project
    {
        public string Name { get; }
        public string Branch { get; }
        public string PublishDir { get; }
        public string PostDeployBatchFile { get; }

        public string ProjectPath { get; }
        public string ProjectFile { get; } = string.Empty;
        public string ProjectExtension { get; } = string.Empty;

        public Project(string name, string projectfilepath, string branch, string publishdir, string postdeploybatchfile)
        {
            Name = name;
            Branch = branch;
            PublishDir = publishdir;
            PostDeployBatchFile = postdeploybatchfile;

            if (File.Exists(projectfilepath))
            {
                ProjectPath = Path.GetDirectoryName(projectfilepath)!;
                ProjectFile = projectfilepath;
                ProjectExtension = Path.GetExtension(projectfilepath);
            }
            else
            {
                ProjectPath = projectfilepath;
            }
        }

        /// <summary>
        /// Perform a project deployment
        /// </summary>
        public bool Deploy()
        {
            bool deployed = false;

            modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Deploying project '{Name}'", Program.logMethod);

            if (Pull())
            {
                if (!CanBuild())
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

            if (deployed)
            {
                // update DeploymentQueued to 0
                var command = new SqlCommand();
                command.Connection = modDatabase.Connection(Program.connectionString);
                command.CommandType = System.Data.CommandType.Text;
                command.CommandText = "UPDATE HuntHome.dev.Repositories SET DeploymentQueued = 0, LastDeployedDate = GETDATE() WHERE RepoName = @RepoName";
                command.Parameters.AddWithValue("@RepoName", Name);
                command.ExecuteNonQuery();

                command.Dispose();

                // install possible dependencies
                deployed = InstallDependencies();

                // execute post-deployment script if applicable
                if (deployed && CanPostDeploy())
                {
                    if (!PostDeploy())
                    {
                        deployed = false;
                    }
                }
            }

            if (deployed)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.INFO, $"Project '{Name}' deployment succeeded", Program.logMethod);
            }
            else
            {
                // do not to indicate the specific error here, those are handled in the individual methods
                modLogging.AddLog(Program.programName, "C#", "Project.Deploy", modLogging.eLogLevel.WARNING, $"Project '{Name}' deployment failed", Program.logMethod);
            }

            return deployed;
        }

        /// <summary>
        /// Determine if the project language supports builds
        /// </summary>
        internal bool CanBuild()
        {
            if (ProjectFile == string.Empty)
            {
                // this generally would only occur if the ProjectFilePath value in the database is a directory, i.e. a Python project
                return false;
            }

            // due to the above check, we do not need to switch against string.Empty
            bool canBuild = true;
            switch (ProjectExtension.ToLower())
            {
                case ".vbproj":
                case ".csproj":
                    break;
                case ".pyproj":
                    canBuild = false;
                    break;
                default:
                    modLogging.AddLog(Program.programName, "C#", "Project.CanBuild", modLogging.eLogLevel.ERROR, $"Project '{Name}' has an unsupported project file extension '{ProjectExtension}'", Program.logMethod);
                    canBuild = false;
                    break;
            }

            if (canBuild)
            {
                if (!String.IsNullOrWhiteSpace(PublishDir) && !Directory.Exists(PublishDir))
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.CanBuild", modLogging.eLogLevel.ERROR, $"Project '{Name}' has an invalid publish directory '{PublishDir}'", Program.logMethod);
                    canBuild = false;
                }
            }

            return canBuild;
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
            Int32 exitCode = modCommandLine.RunCommand($"git pull origin {Branch}", ProjectPath);
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
            Int32 exitCode = modCommandLine.RunCommand("dotnet build -c Release", ProjectPath);
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
                // TODO: do I want to switch to self-contained publishes?
                Int32 exitCode = modCommandLine.RunCommand($"dotnet publish {Name}{ProjectExtension} -c Release --no-build -o \"{PublishDir}\"", ProjectPath);
                if (exitCode != 0)
                {
                    modLogging.AddLog(Program.programName, "C#", "Project.Publish", modLogging.eLogLevel.ERROR, $"Project '{Name}' publish failed", Program.logMethod);
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
            switch (ProjectExtension.ToLower())
            {
                case ".pyproj":
                case "":
                    // assuming if the extension doesn't exist, the project path was a directory and it is likely a Python project
                    if (File.Exists(Path.Combine(ProjectPath, "requirements.txt")))
                    {
                        Int32 exitCode = modCommandLine.RunCommand($"pip install -r requirements.txt", ProjectPath);
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
            Int32 exitCode = modCommandLine.RunCommand(PostDeployBatchFile, ProjectPath);  // likely don't need this in the project directory, but will be consistent
            if (exitCode != 0)
            {
                modLogging.AddLog(Program.programName, "C#", "Project.PostDeploy", modLogging.eLogLevel.ERROR, $"Post-deploy for project '{Name}' failed", Program.logMethod);
                return false;
            }
            return true;
        }
    }
}
