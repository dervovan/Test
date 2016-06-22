using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Threading;
using System.Web;
using System.Web.Mvc;
using NCruiseControl.Domain.Abstract.Providers;
using NCruiseControl.Domain.Abstract.Repos;
using NCruiseControl.Domain.Entities;
using NCruiseControl.Domain.Entities.Autotests;
using NCruiseControl.Domain.Helpers;
using NCruiseControl.Model;
using NLog;

namespace NCruiseControl.Controllers
{
    public class AutotestController : Controller
    {
        private readonly IAutoTestProvider _autoTestProvider;
        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly IBuildFullInfoRepo _buildFullInfoRepo;
        private readonly IBranchFullInfoRepo _branchFullInfoRepo;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public AutotestController(
            IFileSystemProvider fileSystemProvider,
            IBuildFullInfoRepo buildFullInfoRepo,
            IBranchFullInfoRepo branchFullInfoRepo,
            IAutoTestProvider autoTestProvider)
        {
            _autoTestProvider = autoTestProvider;
            _fileSystemProvider = fileSystemProvider;
            _buildFullInfoRepo = buildFullInfoRepo;
            _branchFullInfoRepo = branchFullInfoRepo;
        }

        public ActionResult EsxStands()
        {
            var model = new List<EsxStand>();
            try
            {
                model = _autoTestProvider.GetEsxStandsList(HttpContext.User.Identity.Name);
            }
            catch (Exception ex)
            {
                TempData.Add("error", ex.Message);
            }

            return View(model);
        }

        public ActionResult Index()
        {
            return RedirectToAction("EsxStands");
        }

        [HttpGet]
        public ActionResult AutotestManagement()
        {
            var model = new RunningAutotestModel();
            model.ExistingBranches = _branchFullInfoRepo.BranchCache.Keys.ToList();
            model.ExistingBranches.ForEach(o =>
                {
                    model.RunningBuilds.Add(o, GetActiveAutotestBuilds(o, _buildFullInfoRepo.GetFullBuildInfoListByBranchName(o))); 
                });
            return View(model);
        }

        /// <summary>
        /// настройка автотестов для ветки и запуск
        /// </summary>
        /// <param name="branch"></param>
        /// <returns></returns>
        public ActionResult AutotestSettingsMaster(string branch)
        {
            ViewBag.Branch = branch;
            ViewBag.Builds = _buildFullInfoRepo
                                .GetFullBuildInfoListByBranchName(branch)
                                //.Where(o => o.IsSuccess && o.DistrExists)
                                .Select(o => o.Info.Version);
            var model = new AutotestSettingsMasterModel();
            model.TestList = _autoTestProvider.GetAllAutotestFromDefault(branch);
            model.NotifyList = _autoTestProvider.GetNotifyListFromDefault(branch);
            return View(model);
        }

        public ActionResult StopAutotest(string branch, string build)
        {
            var result = new StopAutotestTransferEntity();
            string eventName = "Global\\CubeAutotestLauncherStopEvent_" + branch + "_" + build;
            try
            {
                using (var @event = EventWaitHandle.OpenExisting(eventName, EventWaitHandleRights.Modify))
                {
                    @event.Set();
                    @event.Close();
                } 
            }
            catch (WaitHandleCannotBeOpenedException ex)
            {
                // нет события, и хорошо
                result.Success = true;
                result.Message = ex.Message;
                return new JsonResult { Data = result };
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "При обновлении глобального события \"" + eventName + "\" произошла ошибка:" + ex.Message;
                _logger.Error(ex.Message);
                return new JsonResult { Data = result };
            }

            result.Success = true;
            result.Message = "Ok";
            return new JsonResult { Data = result };
        }

        public ActionResult StopAllAutotest()
        {
            var builds = _buildFullInfoRepo
                        .BuildCache
                        .Values
                        .Select(o => o.Dir.Parent.Name + "_" + o.Dir.Name)
                        .ToList();
            List<string> error = new List<string>();
            builds.ForEach(o =>
                {
                    string eventName = "Global\\CubeAutotestLauncherStopEvent_" + o;
                    try
                    {
                        using (var @event = EventWaitHandle.OpenExisting(eventName, EventWaitHandleRights.Modify))
                        {
                            @event.Set();
                            @event.Close();
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // нет события - ну и ладно
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex.Message);
                        error.Add("ошибка при обновлении события " + eventName + " - " + ex.Message);
                    }
                });
            TempData.Add("error", error);
            //// подождем на всякий случай
            //// Thread.Sleep(1000);
            return RedirectToAction("AutotestManagement");
        }

        private List<RunningAutotest> GetActiveAutotestBuilds(string branchName, List<BuildFullInfo> builds)
        {
            var result = new List<RunningAutotest>();
            foreach (BuildFullInfo buildFullInfo in builds)
            {
                string eventName = "Global\\CubeAutotestLauncherStopEvent_" + branchName + "_" + buildFullInfo.Dir.Name;
                try
                {
                    using (EventWaitHandle.OpenExisting(eventName, EventWaitHandleRights.ReadPermissions))
                    {
                        
                    }
                    _logger.Error("found autotest event = " + eventName);
                    var totalTestNode = buildFullInfo.Autotests.FirstOrDefault(o => o.ID == "total");
                    string upTime = totalTestNode != null
                                        ? totalTestNode.Duration
                                        : string.Empty;
                    result.Add(new RunningAutotest { Branch = branchName, Build = buildFullInfo.Dir.Name, UpTime = upTime });
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    _logger.Error("no autotest event = " + eventName);
                }
                catch (Exception ex)
                {
                    _logger.Error("error get autotest event = " + eventName);
                    _logger.Error("error get autotest event exeption = " + ex.Message);
                }
            }

            return result;
        }

        public ActionResult SaveTestForBranch(object[] Autotest, object[] NotifyUsers, string branch, string build)
        {
            List<string> tests = Autotest.Select(o => o.ToString()).ToList();
            _fileSystemProvider.CreateNewLauncherXml(tests, branch);
            if (NotifyUsers != null)
            {
                List<string> users = NotifyUsers.Select(o => o.ToString()).ToList();
                _fileSystemProvider.CreateNewEmailXml(users, branch);
            }
            string parameters = branch + " " + build;
            string strCommandParameters = "start " + parameters;
            _logger.Info("SaveTestForBranch params = " + strCommandParameters);
            _autoTestProvider.RunAutotest(branch, strCommandParameters, HttpContext.User.Identity.Name);
            return RedirectToAction("AutotestManagement");
        }

        public ActionResult ModifyStand(string name)
        {
            var res = new ModifyStandTransferEntity();
            try
            {
                var stand = _autoTestProvider.GetEsxStandsList(HttpContext.User.Identity.Name).FirstOrDefault(o => o.StandName == name);

                if (stand.CanTake)
                {
                    _autoTestProvider.TakeEsxStand(HttpContext.User.Identity.Name, stand);
                    res.BusyBy = HttpContext.User.Identity.Name;
                }
                else
                {
                    _autoTestProvider.ReleaseEsxStand(stand);
                    res.BusyBy = "Свободен";
                }
                res.buttonText = stand.GetButtonText();
                res.Success = true;
            }
            catch (UnauthorizedAccessException)
            {
                res.Success = false;
                res.Message = "У пользователя cube-builder забрали права на изменение файла esx_stands.xml";
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Message = ex.Message;
            }
            //var res = new TransferEntity{user = "Vova", buttonText = "test"};
            return new JsonResult() {Data = res};
        }

        public void UpdateStandComment(string name, string text)
        {
            _autoTestProvider.UpdateStandComment(name,text);
        }

        public class ModifyStandTransferEntity
        {
            public string BusyBy { get; set; }
            public string buttonText { get; set; }
            public string Message { get; set; }
            public bool Success { get; set; }
        }

        public class StopAutotestTransferEntity
        {
            public string Message { get; set; }
            public bool Success { get; set; }
        }
    }
}
