using System;
using System.Configuration;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using System.Threading;
using System.Web;
using System.Net;
using System.Web.Mvc;
using System.Web.Script.Serialization;
using NCruiseControl.Controllers.ModelServiceClasses;
using NCruiseControl.Domain.Abstract.Repos;
using NCruiseControl.Domain.Abstract.Providers;
using NCruiseControl.Domain.Concrete;
using NCruiseControl.Domain.Concrete.Providers;
using NCruiseControl.Domain.Concrete.Repos;
using NCruiseControl.Domain.Entities;
using NCruiseControl.Domain.Entities.Autotests;
using NCruiseControl.Domain.Helpers;
using NCruiseControl.Model;
using NLog;

namespace NCruiseControl.Controllers
{

    public class HomeController : Controller 
    {
        private readonly ILastRunRepo _lastRunRepo;
        private readonly IBuildFullInfoRepo _buildFullInfoRepo;
        private readonly IBranchFullInfoRepo _branchFullInfoRepo;
        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly IAutoTestProvider _autoTestProvider;

        /// <summary>
        /// репозиторий статусов запуска веток
        /// пришлось хранить в памяти, иначе непонятно, запущена ветка или нет
        /// по логам решили не смотреть, т.к. при перезапуске сервера не сможем определить статус
        /// </summary>
        private IRunningBranchRepo _runnungBranchRepo;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        
        public HomeController(ILastRunRepo lastRunRepo,
            IBuildFullInfoRepo buildFullInfoRepo, 
            IBranchFullInfoRepo branchFullInfoRepo,
            IFileSystemProvider fileSystemProvider,
            IAutoTestProvider autoTestProvider,
            IRunningBranchRepo runnungBranchRepo)
        {
            _fileSystemProvider = fileSystemProvider;
            _lastRunRepo = lastRunRepo;
            _buildFullInfoRepo = buildFullInfoRepo;
            _branchFullInfoRepo = branchFullInfoRepo;
            _autoTestProvider = autoTestProvider;
            _runnungBranchRepo = runnungBranchRepo;
        }

        [HttpPost]
        public ActionResult CreateBranch(string newBranchName)
        {
            _logger.Info("CreateBranch enter");
            const string emptyString = "Имя создаваемой ветки пустое. Будь внимательнее, ешь витамины.";

            if (String.IsNullOrEmpty(newBranchName)) // пустое имя ветки
            {
                TempData.Add("currentState", emptyString);
                return RedirectToAction("Index");
            }

            _runnungBranchRepo.AddBranch(newBranchName);

            _logger.Info("CreateBranch dummy");
            _branchFullInfoRepo.AddDummyBranchForCreationTime(newBranchName);

            ////_logger.Info("CreateBranch creating thread");
            ////Thread creatingThread = new Thread(() =>
            ////{
            ////    RunBuildWorkerBase creatingWorker = new RunBuildWorkerBase(
            ////        _fileSystemProvider,
            ////        _runnungBranchRepo,
            ////        newBranchName, 
            ////        HttpContext.User.Identity.Name, 
                    
            ////        );

            ////    //creatingWorker.ProcessEnd += () => { };
            ////    creatingWorker.DoWork();
            ////});
            //creatingThread.Start();

            _logger.Info("CreateBranch redirect");

            return RedirectToAction("Index");
        }

        public ActionResult Index()
        {
            _logger.Info("Main requested by " + HttpContext.User.Identity.Name);

            List<BranchFullInfo> branchInfos = _branchFullInfoRepo.GetBranchFullInfoList();

            // показатели работы CPU, RAM, HDD
            PerformanceCounter cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            PerformanceCounter ram = new PerformanceCounter("Memory", "Available MBytes");
            cpu.NextValue();
            System.Threading.Thread.Sleep(500);
            ViewBag.Cpu = (int)cpu.NextValue();
            ViewBag.Ram = ram.NextValue() + " Mb";
            var setting = ConfigurationManager.AppSettings["DisplayDiskSpace"].Split(new[]{','}).Select(o => o.Trim()).Where(o => !string.IsNullOrEmpty(o));
            var disks = new List<DiskSystemInfo>();
            setting.ToList().ForEach(o => disks.Add(_fileSystemProvider.DiskFreeSpace(o)));
            ViewBag.Disks = disks.Where(o => o.SpaceAvailable >= 0).OrderBy(o=>o.DiskName);
            return View(branchInfos.OrderBy(o => o.Dir.Name));
        }

        [HttpGet]
        public ActionResult GetTPLinks( string branch, string build, string test )
        {
            String branchPath = _fileSystemProvider.PathCombine(branch);
            String buildPath = _fileSystemProvider.PathCombine(branchPath, HttpUtility.UrlDecode(build));
            _logger.Info(buildPath + " requested by " + HttpContext.User.Identity.Name);
            BuildFullInfo buildFullInfo = _buildFullInfoRepo.GetBuildFullInfo(buildPath);

            if (buildFullInfo == null)
                return new HttpNotFoundResult();
            
            WebClient _webClient = new System.Net.WebClient();
            //_webClient.Credentials = new NetworkCredential("admin", "admin");

            AutoTestResult testResult =  buildFullInfo.Autotests.Where(testId => testId.ID == test).First();
            foreach(var item in testResult.FailedScripts)
	        {
                //string scriptName = item.ScriptPath.Substring(item.ScriptPath.LastIndexOf('\\', item.ScriptPath.LastIndexOf('\\') - 1) + 1);
                string dirName = Path.GetDirectoryName(item.ScriptPath);
                int pos1 = dirName.LastIndexOf('\\'), pos2 =  dirName.LastIndexOf('\\', pos1 - 1 );
                string dirName1 = dirName.Substring(dirName.LastIndexOf('\\') + 1), dirName2 = dirName.Substring( pos2 + 1, pos1 - pos2 - 1 );

                string tpRequest = "http://qb/tp/api/v1/Bugs?where=(Description contains '" + dirName1 + "')and(Description contains '" + dirName2 + "')and(Description contains '" +
                    Path.GetFileName(item.ScriptPath) + "')and(EntityState.IsFinal ne 'true')&format=json&include=[Id,EntityState[Name]]&token=MTo2Q0FEN0RBNUZERkZFOEMzNzA3ODU1OTY4NTY2QThCQw==";
                
                _webClient.Encoding = System.Text.Encoding.UTF8;
                string answer = _webClient.DownloadString(tpRequest);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                BugCollection bugs = serializer.Deserialize<BugCollection>(answer);
                item.Bugs = bugs;
                
            }

            return View("Autotests", testResult); 
        }

        [HttpPost]
        public ActionResult RestartAutoTests(string[] Autotest)
        {
            var key = HttpContext.Request.Params.AllKeys.FirstOrDefault(o => o.StartsWith("RunTest"));
            
            // при сабмите с несколькими выделенными автотестами, нужно проверить, что 
            // нажали иемнно "запустить все"
            if (String.IsNullOrEmpty(key) && Autotest != null)
            {
                // потом скажут что тут делать 
            }
            
            if (!String.IsNullOrEmpty(key))
            {
                key = key.TrimEnd('.', 'x');
                var testNumber = key.Substring("RunTest".Length);
                var pathToScript = HttpContext.Request["valFor" + testNumber];
                // потом скажут что тут делать 
            }
            
            // тут наверно другое поведение захотят, например, никуда не уходить с текущей страницы.
            // может тут аякс нужен?
            return RedirectToAction("Index");
        }

        /// <summary>
        /// перед показом страницы с веткой записываем, кто запустил билд
        /// </summary>
        /// <param name="branch">имя ветки</param>
        /// <param name="branchPath">полный путь к папке с логами билдов ветки</param>
        private void BeforeRedirect(string branch, string branchPath)
        {
            Thread.Sleep(5000); // не успевает обновиться ласт билд :(
            String changeList = CommonHelper.GetCurrentBuild(branchPath);

            _lastRunRepo.Add(branch, changeList, HttpContext.User.Identity.Name);
            _lastRunRepo.Commit();
        }

        [PermissionSetAttribute(SecurityAction.LinkDemand, Name = "FullTrust")]
        public ActionResult Branch(string branch, string operation)
        {
            ViewBag.BranchName = branch;
            String branchPath = _fileSystemProvider.PathCombine(branch);
            String stdOut = String.Empty;
            _logger.Info(branch + " requested by " + HttpContext.User.Identity.Name);

            //stdOut += _fileSystemProvider.ProcessStart("task.cmd", branch);
            stdOut += _fileSystemProvider.ProcessStart("changes.cmd", branch);

            if (!String.IsNullOrEmpty(operation))
            {
                switch (operation)
                {
                    case "rbtfc":
                        // уже запускали выше
                        //stdOut += _fileSystemProvider.ProcessStart("task.cmd", branch);
                        break;
                    case "run":
                        
                        RunBuild(branch, branchPath);
                        return RedirectToAction("Branch", new { branch });
                    case "run_coverage":

                        RunBuild(branch, branchPath, true);
                        return RedirectToAction("Branch", new { branch });
                    case "rbcfc":
                        // уже запускали выше
                        //stdOut += _fileSystemProvider.ProcessStart("changes.cmd", branch);
                        break;
                    case "dir":
                        // вроде не используется
                        stdOut += _fileSystemProvider.ProcessStart("dir.cmd", String.Empty);
                        break;
                    case "del":
                        _logger.Info(HttpContext.User.Identity.Name + " deleting branch " + branch);
                        _branchFullInfoRepo.DeleteBranch(branch);
                        return RedirectToAction("Index");

                    case "runAutotest":
                        string parameters = _fileSystemProvider.GetAutotestStartParameters(branch);
                        string strCommandParameters = "start " + parameters;
                        _logger.Info("runAutotest parameters = " + strCommandParameters);
                        _autoTestProvider.RunAutotest(branch, strCommandParameters, HttpContext.User.Identity.Name);
                        return RedirectToAction("Index");

                    case "AutoSettings":
                        return RedirectToAction("AutotestSettingsMaster", "Autotest", new {branch});
                }
            }

            BranchFullInfo branchFullInfo = _branchFullInfoRepo.GetBranchFullInfo(branchPath);
            IEnumerable<LastRun> lastRuns = _lastRunRepo.LastRuns(branch).ToList();
            List<BuildFullInfo> buildInfos = _buildFullInfoRepo.GetFullBuildInfoList(branchPath);
            //buildInfos.ForEach(o => o.RewriteOwner(lastRuns));
           
            ViewBag.Branch = branch;
            ViewBag.Lines = buildInfos;
            ViewBag.BranchFullInfo = branchFullInfo;
            ViewBag.stdOut = stdOut;
            ViewBag.LastRunners = lastRuns.OrderByDescending(o => o.When);

            return View();
        }

        /// <summary>
        /// Запуск билда. Сам разберется, стримовая ветка или нет.
        /// </summary>
        /// <param name="branch">имя ветки</param>
        /// <param name="branchPath">полный путь к папке с логами билдов ветки</param>
        /// <param name="CoverageRun">дополнительный параметр сборки </param>
        private void RunBuild(string branch, string branchPath, bool CoverageRun = false)
        {
            _runnungBranchRepo.AddBranch(branch);

            Thread runBuildThread = new Thread(() =>
            {
                try
                {
                    string coverage = CoverageRun ? " /coverage" : string.Empty;
                    var perforce = new PerforceController();

                    if (_fileSystemProvider.IsStreamBranch(branch))
                    {
                        string workspaceName;
                        if (perforce.IsStreamWorkspaceExists(branch, out workspaceName))
                        {
                            string[] data = _fileSystemProvider.ReadBranchRunParams(branch, true);
                            if (data.Length > 1)
                            {
                                perforce.RunBuild(data[1], data[0], branch);
                            }
                            else
                            {
                                string sourcePath = _fileSystemProvider.GetBranchSourceCodeFullPath(branch);
                                string executingFile = sourcePath + "\\Cube\\build.cmd";
                                string runParams = " /mt /no_errors /fast /branch " + branch + " /client " + workspaceName + coverage;
                                perforce.RunBuild(runParams, executingFile, branch);
                            }
                        }
                        else
                        {
                            _logger.Info("ERROR: can't find workspace for stream branch = " + branch);
                        }
                    }
                    else
                    {
                        string[] data = _fileSystemProvider.ReadBranchRunParams(branch, false);
                        if (data.Length > 1)
                        {
                            perforce.RunBuild(data[1], data[0], branch);
                        }
                        else if (data.Length == 1)
                        {
                            string runParams = "/mt /no_errors /fast /branch " + branch + " /buildroot " + _fileSystemProvider.BuildsPath + "\\" + branch;
                            _logger.Info("Run not stream build build.cmd = " + data[0]);
                            _logger.Info("Run not stream build params = " + runParams);
                            perforce.RunBuild(runParams, data[0], branch);
                        }
                        else 
                        {
                            _logger.Info("Run not stream build. NotStream file is empty");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Info("RunBuild ex = " + ex.Message);
                }
                finally 
                {
                    _runnungBranchRepo.RemoveBranch(branch);
                }
            });

            runBuildThread.Start();
            this.BeforeRedirect(branch, branchPath);
        }

        /// <summary>
        /// окно с информацией для конкретного билда
        /// </summary>
        /// <param name="branch"></param>
        /// <param name="build"></param>
        /// <returns></returns>
        public ActionResult Build(string branch, string build)
        {
            String branchPath = _fileSystemProvider.PathCombine(branch);
            String buildPath = _fileSystemProvider.PathCombine(branchPath, HttpUtility.UrlDecode(build));
            _logger.Info(buildPath + " requested by " + HttpContext.User.Identity.Name);
            BuildFullInfo buildFullInfo = _buildFullInfoRepo.GetBuildFullInfo(buildPath);

            if (buildFullInfo == null)
            {
                return new HttpNotFoundResult();
            }

            //_logger.Info("buildFullInfo.Dir.FullName = " + buildFullInfo.Dir.FullName);
            //buildFullInfo.RewriteOwner((_lastRunRepo.LastRuns(branch)));

            ViewBag.Branch = branch;
            ViewBag.Build = build;
            ViewBag.Title = String.Format("{0}/{1}", branch, build);
            //_logger.Info("Было ли вычислено значение = " + buildFullInfo.Autotests.IsValueCreated);
            //_logger.Info("Размер лога автотестов = " + buildFullInfo.Autotests.Value.Length);
           
            return View(buildFullInfo);
        }

        public ActionResult GetFile(string branch, string build, string logfile, bool? download)
        {
            if (String.IsNullOrEmpty(branch) || String.IsNullOrEmpty(build) || String.IsNullOrEmpty(logfile))
            {
                return new HttpNotFoundResult();
            }
            //string filePath = System.Configuration.ConfigurationManager.AppSettings["PathToBuild"];
            string filePath = _fileSystemProvider.PathCombine(branch);
            filePath = _fileSystemProvider.PathCombine(filePath, build);
            filePath = _fileSystemProvider.PathCombine(filePath, logfile);
            if (!_fileSystemProvider.FileExists(filePath))
            {
                return new HttpNotFoundResult();
            }

            if (download.HasValue && download.Value)
            {
                FilePathResult res = new FilePathResult(filePath, "text/plain");
                res.FileDownloadName = _fileSystemProvider.GetFileName(filePath);
                //Response.AddHeader("Content-Disposition", "attachment; filename=" + Path.GetFileName(filePath));
                return res;
            }
            ContentResult result = new ContentResult();
            result.Content = _fileSystemProvider.ReadAllText(filePath);
            result.ContentType = "text/plain";
            return result;
        }

        /// <summary>
        /// окно запуска новой сборки с параметрами
        /// </summary>
        /// <returns></returns>
        public ActionResult StartBuildWithParms()
        {
            ViewBag.RunningBuilds = _runnungBranchRepo.GetRunningBranches();
            return this.View();
        }

        /// <summary>
        /// запуск процесса сборки ветки
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult StartBuildWithParms(BuildByParamsModel model)
        {
            ViewBag.RunningBuilds = _runnungBranchRepo.GetRunningBranches();

            if (!ModelState.IsValid)
            {
                return View();
            }

            string branchName;
            string parameters = GetParamStringFromBuildByParamsModel(model, out branchName);

            if (_runnungBranchRepo.IsRunning(branchName))
            {
                ViewBag.ErrorMessage = "Сборка для указанной ветки уже запущена";
                return View();
            }

            if (string.IsNullOrEmpty(branchName))
            {
                if (!model.IsStream.Value) // не стримомвая 
                {
                    ViewBag.ErrorMessage = "Наверно неверно указан тип ветки? Может, стримовая?";
                }
                else // стримовая
                {
                    ViewBag.ErrorMessage = "Что-то с путем ветки не то. Обратитесь к администратору.";
                }

                return View();
            }

            if (!_branchFullInfoRepo.BranchCache.ContainsKey(branchName))
            {
                _logger.Info("CreateBranch dummy");
                _branchFullInfoRepo.AddDummyBranchForCreationTime(branchName);
            }

            _runnungBranchRepo.AddBranch(branchName);

            Thread buildingThread = new Thread(() =>
            {
                RunBuildWorkerBase buildWorker;
                if (model.IsStream.HasValue && model.IsStream.Value)
                {
                    buildWorker = new RunStreamBuildWorker(
                        _fileSystemProvider,
                        _runnungBranchRepo,
                        branchName,
                        HttpContext.User.Identity.Name,
                        parameters);
                }
                else
                {
                    buildWorker = new RunNotStreamWorker(
                        _fileSystemProvider,
                        _runnungBranchRepo,
                        branchName,
                        HttpContext.User.Identity.Name,
                        parameters,
                        model.path);
                }

                buildWorker.DoWork();
            });
            buildingThread.Start();

            ///_buildsByPathRepo.AddPath(model.path);

            // PerforceController perforce = new PerforceController();
            // string buildCmdPath = perforce.GetBuildCmdPath(model.path);
            // if (buildCmdPath.Split(' ')[0] == "error")
            // {
            //    ModelState.AddModelError("", buildCmdPath);
            //    //ViewBag.Error = "Указан неверный путь";
            //    _buildsByPathRepo.DeletePath(model.path);
            //    return View();
            // }
 
            //string branchName;

            //Thread buildingThread = new Thread(() =>
            //    {
            //        try
            //        {
            //            PerforceController perforce = new PerforceController();
            //            _logger.Info("build by path params = " + parameters);
            //            perforce.SyncWorkspaceByPath(model.path);
            //            string buildCmdPath = perforce.GetBuildCmdPath(model.path);
            //            if (buildCmdPath.Split(' ')[0] == "error")
            //            {
            //                _logger.Info("BuildByPath branch: " + buildCmdPath);
            //                _runnungBranchRepo.RemoveBranch(branchName);
            //                //_buildsByPathRepo.DeletePath(model.path);
            //                return ;
            //            }
            //            _fileSystemProvider.CommitRunParamsForLaterUse(buildCmdPath, model.path, parameters);
            //            perforce.RunBuild(parameters, buildCmdPath, branchName);
            //        }
            //        catch (Exception ex)
            //        {
            //            _logger.Info("buildingThread ex = " + ex.Message);
            //        }
            //        finally 
            //        {
            //            //_buildsByPathRepo.DeletePath(model.path);
            //            _runnungBranchRepo.RemoveBranch(branchName); 
            //        }
            //    });
            //buildingThread.Start();

            //ViewBag.RunningBuilds = _buildsByPathRepo.GetRunningBuildPaths.ToList();
            return RedirectToAction("Index");
        }

        private string GetParamStringFromBuildByParamsModel(BuildByParamsModel model, out string branchName)
        {
            string result = string.Empty;

            // валидация не дает этому случится
            if (!model.IsStream.HasValue)
            {
                branchName = string.Empty;
                return result;
            }
            
            if (model.noErrors)
            {
                result += " /no_errors";
            }

            if (model.parallel)
            {
                result += " /mt";
            } 

            if (model.rebuild)
            {
                result += " /rebuild";
            }

            if (model.fast)
            {
                result += " /fast";
            }

            if (!string.IsNullOrEmpty(model.additionalParams))
            {
                result += " " + model.additionalParams;
            }

            branchName = model.IsStream.Value
                            ? model.path
                            : string.Join(string.Empty, model.path.TrimStart('/').Split('/').Skip(1).ToList());
            result += " /branch " + branchName;
            if (!model.IsStream.Value)
            {
                result += " /buildroot " + CommonHelper.PathToBuild + "\\" + branchName;
            }

            return result;
        }

        public ActionResult Cache()
        {
            _logger.Info("Cache requested by " + HttpContext.User.Identity.Name + "  ----------------------------- ");
            Dictionary<BranchFullInfo, List<BuildFullInfo>> data = 
                new Dictionary<BranchFullInfo, List<BuildFullInfo>>();
            
            _branchFullInfoRepo.BranchCache.ToList().ForEach(o =>
                {
                    List<BuildFullInfo> buildList = new List<BuildFullInfo>();
                    buildList = _buildFullInfoRepo.BuildCache.Values
                                .Where(build => build != null 
                                                && build.Dir.Parent.Name == o.Value.Dir.Name)
                                .ToList();
                    data.Add(o.Value, buildList);
                });
            
            return View(data);
        }

        /// <summary>
        /// тест
        /// </summary>
        /// <returns></returns>
        public ActionResult CreateTestFile()
        {
            using (Process myProcess = new Process())
            {
                myProcess.StartInfo.FileName = @"D:\qb-proj\dir.bat";
                myProcess.StartInfo.Arguments = "> dir.txt";
                myProcess.StartInfo.UserName = "cube-builder";
                SecureString passwd = new SecureString();
                "Gh0,ktvf2+".ToCharArray().ToList().ForEach(passwd.AppendChar);
                myProcess.StartInfo.Password = passwd;
                myProcess.StartInfo.Domain = "cube.infosec.ru";
                myProcess.StartInfo.WorkingDirectory = "D:\\qb-proj";
                myProcess.StartInfo.UseShellExecute = false;
                myProcess.StartInfo.CreateNoWindow = true;
                myProcess.Start();
            }
            return RedirectToAction("Index", "Home");
        }

        public ActionResult NotFound()
        {
            return View();
        }
    }
   
}
