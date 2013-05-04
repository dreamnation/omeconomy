/*
 * Michael E. Steurer, 2011
 * Institute for Information Systems and Computer Media
 * Graz University of Technology
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using LitJson;
using Mono.Addins;
using OpenMetaverse;
using OpenSim;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("OMBaseModule", "0.1")]
[assembly: AddinDependency("OpenSim", "0.5")]

namespace OMEconomy.OMBase
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class OMBaseModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static Dictionary<UUID,OMBaseModule> regionInstances = new Dictionary<UUID,OMBaseModule>();

        private bool Enabled = false;

        private string gridURL = String.Empty;
        private string gridShortName = String.Empty;
        private string gridID = String.Empty;
        internal String gatewayURL = String.Empty;
        private String initURL = String.Empty;
        private String gatewayEnvironment = String.Empty;
        private string regionAddress;
        private string regionName;
        private string regionSecret;
        private UUID   regionUUID;

        private String MODULE_VERSION = "4.0.3";

        private delegate void delegateAsynchronousClaimUser(String gatewayURL, Dictionary<string, string> data);


        #region ISharedRegion implementation

        public string Name
        {
            get { return "OMBase"; }
        }

        public void Initialise(IConfigSource config)
        {
            IConfig cfg = config.Configs["OpenMetaverseEconomy"];

            if (null == cfg)
                return;

            Enabled = cfg.GetBoolean("enabled", false);

            if (!Enabled)
                return;

            if (gatewayURL.Equals(String.Empty))
            {
                gridID = config.Configs["OpenMetaverseEconomy"].GetString("GridID", String.Empty);
                gridURL = config.Configs["GridService"].GetString("GridServerURI", String.Empty);
                gridShortName = config.Configs["OpenMetaverseEconomy"].GetString("GridShortName", String.Empty);

                gridURL = CommunicationHelpers.NormaliseURL(gridURL);

                try
                {
                    IConfig startupConfig = config.Configs["OpenMetaverseEconomy"];
                    gatewayEnvironment = startupConfig.GetString("OMBaseEnvironment", "TEST");
                    initURL = startupConfig.GetString("OMEconomyInitialize", String.Empty);

                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[OMECONOMY]: Exception {0}:[1}", Name, e.Message, e.StackTrace);
                }

                gatewayURL = CommunicationHelpers.GetGatewayURL(initURL, Name, MODULE_VERSION, gatewayEnvironment);
            }

            MainServer.Instance.AddXmlRPCHandler("OMBaseNotification", GenericNotify, false);
        }

        public void PostInitialise() { }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void AddRegion(Scene scene)
        {
            if (!Enabled)
                return;

            SceneHandler.Instance.AddScene(scene);

            regionAddress = CommunicationHelpers.GetRegionAdress(scene);
            regionName    = scene.RegionInfo.RegionName;
            regionUUID    = scene.RegionInfo.originRegionID;

            lock (regionInstances) {
                regionInstances[regionUUID] = this;
            }

            InitializeRegion();

            scene.AddCommand(this, "OMBaseTest", "Test Open Metaverse Economy Connection", "Test Open Metaverse Economy Connection", testConnection);
            scene.AddCommand(this, "OMRegister", "Registers the Metaverse Economy Module", "Registers the Metaverse Economy Module", registerModule);

        }

        public void RegionLoaded(Scene scene)
        {
            if (!Enabled)
                return;

            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
            scene.EventManager.OnClientClosed += OnClientClosed;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled)
                return;

            lock (regionInstances) {
                regionInstances.Remove (regionUUID);
            }

            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
            scene.EventManager.OnClientClosed -= OnClientClosed;

        }

        public void Close()
        {
            if (Enabled)
            {
                List<string> regions = SceneHandler.Instance.GetUniqueRegions().ConvertAll<String>(UUIDToString);
                Dictionary<string, string> d = new Dictionary<string, string>();
                d.Add("method", "closeRegion");
                d.Add("gridURL", gridURL);
                d.Add("gridShortName", gridShortName);
                d.Add("regions", JsonMapper.ToJson(regions));
                CommunicationHelpers.DoRequest(gatewayURL, d);
                regionSecret = null;
            }
        }

        #endregion

        #region // Events
        private void OnMakeRootAgent(ScenePresence sp)
        {
            IClientAPI client = SceneHandler.Instance.LocateClientObject(sp.UUID);
            Scene currentScene = SceneHandler.Instance.LocateSceneClientIn(sp.UUID);

            Dictionary<string, string> dd = new Dictionary<string, string>();
            dd.Add("method", "claimUser");
            dd.Add("avatarUUID", sp.UUID.ToString());
            dd.Add("avatarName", sp.Name);
            dd.Add("language", "ENG");
            dd.Add("viewer", sp.Viewer);
            dd.Add("clientIP", "http://" + client.RemoteEndPoint.ToString() + "/");
            dd.Add("regionUUID", SceneHandler.Instance.LocateSceneClientIn(sp.UUID).RegionInfo.RegionID.ToString());
            dd.Add("gridURL", gridURL);
            dd.Add("gridShortName", gridShortName);
            dd.Add("regionIP", CommunicationHelpers.GetRegionAdress(currentScene));

            delegateAsynchronousClaimUser a = new delegateAsynchronousClaimUser(asynchronousClaimUser);
            a.BeginInvoke(gatewayURL, dd, null, null);
        }

        private void OnClientClosed(UUID clientID, Scene scene)
        {
            Scene sc = SceneHandler.Instance.LocateSceneClientIn(clientID);
            if (sc == null)
                return;

            try
            {
                Dictionary<string, string> d = new Dictionary<string, string>();
                d.Add("method", "leaveUser");
                d.Add("avatarUUID", clientID.ToString());
                d.Add("regionUUID", sc.RegionInfo.RegionID.ToString());
                CommunicationHelpers.DoRequest(gatewayURL, d);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[OMECONOMY]: LeaveAvatar(): {0}", e.Message);
            }
        }
        #endregion

        private string UUIDToString(UUID item)
        {
            return item.ToString();
        }

        public static void InitializeRegion (UUID regionUUID)
        {
            OMBaseModule zhis;
            lock (regionInstances) {
                zhis = regionInstances[regionUUID];
            }
            zhis.InitializeRegion();
        }
        private void InitializeRegion()
        {
            regionSecret = null;

            Dictionary<string, string> d = new Dictionary<string, string>();
            d.Add("method", "initializeRegion");
            d.Add("regionIP", regionAddress);
            d.Add("regionName", regionName);
            d.Add("regionUUID", regionUUID.ToString());
            d.Add("gridURL", gridURL);
            d.Add("gridShortName", gridShortName);
            d.Add("simulatorVersion", VersionInfo.Version);
            d.Add("moduleVersion", MODULE_VERSION);
            Dictionary<string, string> response = CommunicationHelpers.DoRequest(gatewayURL, d);

            if (response == null) {
                m_log.Error ("[OMECONOMY]: error reading region secret");
            } else {
                regionSecret = response["regionSecret"];
                m_log.Info ("[OMECONOMY]: region initialized successfully");
            }
        }


        private void registerModule(string module, string[] args)
        {
            m_log.Info("[OMECONOMY]: +-");
            m_log.Info("[OMECONOMY]: | Your grid identifier is \"" + gridURL + "\"");
            String shortName = MainConsole.Instance.CmdPrompt("           [OMECONOMY]: | Please enter the grid's nick name");
            String longName = MainConsole.Instance.CmdPrompt("           [OMECONOMY]: | Please enter the grid's full name");

            Dictionary<string, string> d = new Dictionary<string, string>();
            d.Add("method", "registerScript");
            d.Add("gridShortName", shortName);
            d.Add("gridLongName", longName);
            d.Add("gridDescription", "");
            d.Add("gridURL", gridURL);

            Dictionary<string, string> response = CommunicationHelpers.DoRequest(gatewayURL, d);
            if (response.ContainsKey("success") && response["success"] == "TRUE")
            {
                m_log.Info("[OMECONOMY]: +-");
                m_log.Info("[OMECONOMY]: | Please visit");
                m_log.Info("[OMECONOMY]: |   " + response["scriptURL"]);
                m_log.Info("[OMECONOMY]: | to get the Terminal's script");
                m_log.Info("[OMECONOMY]: +-");
            }
            else
            {
                m_log.Error("[OMECONOMY]: Could not active the grid. Please check the parameters and try again");
            }
        }

        private void testConnection(string module, string[] args)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d.Add("method", "checkStatus");
            bool status = false;
            try
            {
                Dictionary<string, string> response = CommunicationHelpers.DoRequest(gatewayURL, d);
                if (response.ContainsKey("status") && response["status"] == "INSOMNIA")
                {
                    status = true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[OMECONOMY] - Exception: {0}", e.Message);
            }

            m_log.Info("[OMECONOMY]: +---------------------------------------");
            m_log.Info("[OMECONOMY]: | gridID: " + gridURL);
            m_log.Info("[OMECONOMY]: | connectionStatus: " + status);
            m_log.Info("[OMECONOMY]: +---------------------------------------");
        }

        public static String GetRegionSecret (UUID regionUUID)
        {
            OMBaseModule zhis;
            lock (regionInstances) {
                zhis = regionInstances[regionUUID];
            }
            return zhis.regionSecret;
        }

        /**
         * @brief Runs in a worker thread to tell money server the avatar is present in this region.
         *
         *  <we send a claimUser message to server>
         *      method=claimUser
         *      avatarUUID=e8b4d660-8ef2-49e0-a202-3d1f8f06c2db
         *      avatarName=Kunta Kinte
         *      language=ENG
         *      viewer=Imprudence 1.3.2.0
         *      clientIP=http://173.166.90.236:58410/
         *      regionUUID=c1d50d10-7117-1ff1-b0c4-0800200c9a66
         *      gridURL=http://world.dreamnation.net:8003/
         *      gridShortName=dreamnation
         *      regionIP=http://69.25.198.248:9900/
         *  <server sends us a genericNotify callback>
         *      method notifyIsAlive
         *      avatarUUID e8b4d660-8ef2-49e0-a202-3d1f8f06c2db
         *      regionUUID c1d50d10-7117-1ff1-b0c4-0800200c9a66
         *      notificationID 3714625
         *      nonce 370619060
         *  <we send a ValidatRequest message for the genericNotify>
         *      method=verifyNotification
         *      notificationID=3714601
         *      regionUUID=c1d50d10-7117-1ff1-b0c4-0800200c9a66
         *      hashValue=dbfe314d251daf47df4341f3942e4f4ad99c14af
         *  <server replies to the ValidateRequest>
         *      {'status' : 'OK'}
         *  <server sends us a currencyNotify callback>
         *      method notifyBalanceUpdate
         *      avatarUUID e8b4d660-8ef2-49e0-a202-3d1f8f06c2db
         *      balance 3677
         *      regionUUID c1d50d10-7117-1ff1-b0c4-0800200c9a66
         *      notificationID 3714603
         *      nonce 3861607714
         *  <we send a ValidatRequest message for the currencyNotify>
         *      method=verifyNotification
         *      notificationID=3714603
         *      regionUUID=c1d50d10-7117-1ff1-b0c4-0800200c9a66
         *      hashValue=fa1bd60a548649f0a238231f515b01cb14992711
         *  <server replies to the ValidateRequest>
         *      {'status' : 'OK'}
         *  <server replies to the claimUser message>
         *      {"success":"true"}
         *
         *  Notes:
         *      1) the server occasionally replies 'NOK' to our ValidateRequest messages.
         *      2) even when it returns 'NOK' to the ValidateRequest messages, the dumbbell
         *         server replies {"success":"true"} to the original claimUser message.
         */
        private void asynchronousClaimUser(String gatewayURL, Dictionary<string, string> data)
        {
            if (CommunicationHelpers.DoRequest(gatewayURL, data) == null) {
                String message = "The currency service is not available. Please try again later.";
                SceneHandler.Instance.LocateClientObject(new UUID(data["avatarUUID"])).SendBlueBoxMessage(UUID.Zero, String.Empty, message);
            }
        }

        public XmlRpcResponse GenericNotify(XmlRpcRequest request, IPEndPoint ep)
        {
            XmlRpcResponse r = new XmlRpcResponse();
            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                Hashtable communicationData = (Hashtable)request.Params[1];

                m_log.Debug("[OMECONOMY]: genericNotify(...)");
                foreach (DictionaryEntry requestDatum in requestData)
                {
                    m_log.Debug("[OMECONOMY]:   " + requestDatum.Key.ToString() + " " + (string)requestDatum.Value);
                }
                foreach (DictionaryEntry communicationDatum in communicationData)
                {
                    m_log.Debug("[OMECONOMY]:   " + communicationDatum.Key.ToString() + " " + (string)communicationDatum.Value);
                }

                String method = (string)requestData["method"];
                requestData.Remove("method");

                if (CommunicationHelpers.ValidateRequest(communicationData, requestData, gatewayURL))
                {
                    switch (method)
                    {
                        case "notifyUser": r.Value = userInteract(requestData);
                            break;
                        case "writeLog": r.Value = WriteLog(requestData);
                            break;
                        case "notifyIsAlive": r.Value = IsAlive(requestData);
                            break;
                        default: m_log.ErrorFormat("[OMECONOMY]: Method {1} is not supported.", Name, method);
                            break;
                    }
                }
                else
                {
                    throw new Exception("Hash values do not match");
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[OMECONOMY]: genericNotify() Exception: {1} - {2}", Name, e.Message, e.StackTrace);
                r.SetFault(1, "Could not parse the requested method");
            }
            return r;
        }

        private Hashtable userInteract(Hashtable requestData)
        {
            Hashtable rparms = new Hashtable();
            try
            {
                UUID receiverUUID = UUID.Parse((string)requestData["receiverUUID"]);
                Int32 type = Int32.Parse((string)requestData["type"]);
                String payloadID = (string)requestData["payloadID"];

                Dictionary<string, string> d = new Dictionary<string, string>();
                d.Add("method", "getNotificationMessage");
                d.Add("payloadID", payloadID);

                Dictionary<string, string> messageItems = CommunicationHelpers.DoRequest(gatewayURL, d);
                if (messageItems == null)
                {
                    throw new Exception("Could not fetch payload with ID " + payloadID);
                }

                m_log.Debug ("[OMECONOMY] userInteract*:");
                foreach (KeyValuePair<string, string> pair in messageItems)
                {
                    m_log.Debug("[OMECONOMY]    " + pair.Key + "  " + pair.Value);
                }

                IClientAPI client = SceneHandler.Instance.LocateClientObject(receiverUUID);
                if (client == null)
                {
                    throw new Exception("Could not locate the specified avatar");
                }

                Scene userScene = SceneHandler.Instance.GetSceneByUUID(client.Scene.RegionInfo.originRegionID);
                if (userScene == null)
                {
                    throw new Exception("Could not locate the specified scene");
                }

                String message = messageItems["message"];

                UUID senderUUID = UUID.Zero;
                String senderName = String.Empty;
                IDialogModule dm = null;
                IClientAPI sender = null;

                IUserManagement userManager = SceneHandler.Instance.GetRandomScene().RequestModuleInterface<IUserManagement>();
                if (userManager == null)
                {
                    throw new Exception("Could not locate UserMangement Interface");
                }

                switch (type)
                {
                    case (int)NotificationType.LOAD_URL:
                        String url = messageItems["url"];

                        dm = userScene.RequestModuleInterface<IDialogModule>();
                        dm.SendUrlToUser(receiverUUID, "OMEconomy", UUID.Zero, UUID.Zero, false, message, url);
                        break;

                    case (int)NotificationType.CHAT_MESSAGE:
                        senderUUID = UUID.Parse(messageItems["senderUUID"]);
                        senderName = userManager.GetUserName(senderUUID);


                        client.SendChatMessage(
                            message, (byte)ChatTypeEnum.Say, Vector3.Zero, senderName,
                            senderUUID, senderUUID, (byte)ChatSourceType.Agent, (byte)ChatAudibleLevel.Fully);

                        sender = SceneHandler.Instance.LocateClientObject(senderUUID);
                        if (sender != null)
                        {
                            sender.SendChatMessage(
                                message, (byte)ChatTypeEnum.Say, Vector3.Zero, senderName,
                                senderUUID, senderUUID, (byte)ChatSourceType.Agent, (byte)ChatAudibleLevel.Fully);
                        }
                        break;

                    case (int)NotificationType.ALERT:
                        dm = userScene.RequestModuleInterface<IDialogModule>();
                        dm.SendAlertToUser(receiverUUID, message);
                        break;

                    case (int)NotificationType.DIALOG:
                        client.SendBlueBoxMessage(UUID.Zero, "", message);
                        break;

                    case (int)NotificationType.GIVE_NOTECARD:
                        break;

                    case (int)NotificationType.INSTANT_MESSAGE:
                        senderUUID = UUID.Parse(messageItems["senderUUID"]);
                        UUID sessionUUID = UUID.Parse(messageItems["sessionUUID"]);
                        if (messageItems.ContainsKey("senderName"))
                        {
                            senderName = messageItems["senderName"];
                        }
                        else
                        {
                            senderName = userManager.GetUserName(UUID.Parse((string)messageItems["senderUUID"]));

                        }

                        GridInstantMessage msg = new GridInstantMessage();
                        msg.fromAgentID = senderUUID.Guid;
                        msg.toAgentID = receiverUUID.Guid;
                        msg.imSessionID = sessionUUID.Guid;
                        msg.fromAgentName = senderName;
                        msg.message = (message != null && message.Length > 1024) ? msg.message = message.Substring(0, 1024) : message;
                        msg.dialog = (byte)InstantMessageDialog.MessageFromAgent;
                        msg.fromGroup = false;
                        msg.offline = (byte)0;
                        msg.ParentEstateID = 0;
                        msg.Position = Vector3.Zero;
                        msg.RegionID = userScene.RegionInfo.RegionID.Guid;


                        client.SendInstantMessage(msg);

                        sender = SceneHandler.Instance.LocateClientObject(senderUUID);
                        if (sender != null)
                        {
                            sender.SendInstantMessage(msg);
                        }
                        break;

                    default:
                        break;
                }

                rparms["success"] = true;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[OMECONOMY]: userInteract() Exception: {1} - {2}", Name, e.Message, e.StackTrace);
                rparms["success"] = false;
            }
            return rparms;
        }

        private Hashtable WriteLog(Hashtable requestData)
        {
            Hashtable rparms = new Hashtable();
            try
            {
                m_log.ErrorFormat("[OMECONOMY]: {1}", requestData["message"]);
                rparms["success"] = true;
            }
            catch (Exception)
            {
                rparms["success"] = false;
            }
            return rparms;
        }

        private Hashtable IsAlive(Hashtable requestData)
        {
            Hashtable rparms = new Hashtable();
            rparms["success"] = false;
            if (requestData.ContainsKey("avatarUUID"))
            {
                UUID avatarUUID = UUID.Parse((string)requestData["avatarUUID"]);
                if (SceneHandler.Instance.LocateClientObject(avatarUUID) != null)
                {
                    rparms["success"] = true;
                }
            }
            else
            {
                rparms["success"] = true;
                rparms["version"] = MODULE_VERSION;
            }
            return rparms;
        }
    }

    public enum NotificationType : int
    {
        LOAD_URL = 1,
        INSTANT_MESSAGE = 2,
        ALERT = 3,
        DIALOG = 4,
        GIVE_NOTECARD = 5,
        CHAT_MESSAGE = 6,
    }
}
