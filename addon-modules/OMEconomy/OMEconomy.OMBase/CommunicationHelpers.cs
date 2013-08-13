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

using System.Collections.Generic;
using OpenMetaverse;
using System;
using System.Collections;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using log4net;
using System.Reflection;
using System.Text;
using System.Net;
using System.IO;
using LitJson;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace OMEconomy.OMBase
{
    public class CommunicationHelpers
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static String NormaliseURL(String url)
        {
            url = url.EndsWith("/") ? url : (url + "/");
            url = url.StartsWith("http://") ? url : ("http://" + url);
            return url;
        }

        public static String GetRegionAdress(Scene scene)
        {
            if (scene == null)
                return String.Empty;

            return String.Format("http://{0}:{1}/",
                scene.RegionInfo.ExternalEndPoint.Address.ToString(), scene.RegionInfo.HttpPort.ToString());
        }

        public static String hashParameters(Hashtable parameters, string nonce, UUID regionUUID) {
            StringBuilder concat = new StringBuilder();

            //Ensure that the parameters are in the correct order
            SortedList<string, string> sortedParameters = new SortedList<string, string>();
            foreach(DictionaryEntry parameter in parameters) {
                sortedParameters.Add((string)parameter.Key, (string)parameter.Value);
            }

            foreach( KeyValuePair<string, string> de in sortedParameters) {
                concat.Append((string)de.Key + (string)de.Value);
            }

            String regionSecret = OMBaseModule.GetRegionSecret(regionUUID);
            String message = concat.ToString() + nonce + regionSecret;

            SHA1 hashFunction = new SHA1Managed();
            byte[] hashValue = hashFunction.ComputeHash(Encoding.UTF8.GetBytes(message));

            string hashHex = "";
            foreach(byte b in hashValue) {
                hashHex += String.Format("{0:x2}", b);
            }

            #if DEBUG
                //m_log.Debug(String.Format("[OMECONOMY] SHA1({0}) = {1}", message, hashHex));
            #endif
            return hashHex;
        }

        public static String SerializeDictionary(Dictionary<string, string> data)
        {
            string value = String.Empty;
            foreach (KeyValuePair<string, string> pair in data)
            {
                value += pair.Key + "=" + pair.Value + "&";
            }
            return value.Remove(value.Length - 1);
        }

        public static Dictionary<string, string> DoRequest(string url, Dictionary<string, string> postParameters)
        {
            string postData = postParameters == null ? "" : CommunicationHelpers.SerializeDictionary(postParameters);
            String str = String.Empty;

            #region // Debug
#if DEBUG
            m_log.Debug("[OMECONOMY] Request: " + url + "?" + postData);
#endif
            #endregion

            try
            {
#if INSOMNIA
                ServicePointManager.ServerCertificateValidationCallback = delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) { return true; };
#endif

                str = SynchronousRestFormsRequester.MakeRequest ("POST", url, postData, 20000);

                #region // Debug
#if DEBUG
                string meth = "";
                if ((postParameters != null) && !postParameters.TryGetValue ("method", out meth)) meth = "";
                m_log.DebugFormat("[OMECONOMY] Response {0}: {1}", meth, str.Trim ());
#endif
                #endregion

                Dictionary<string, string> returnValue = JsonMapper.ToObject<Dictionary<string, string>>(str);
                return returnValue != null ? returnValue : new Dictionary<string, string>();

            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[OMECONOMY]: Could not parse response Exception: {0} - {1}", e.Message, e.StackTrace);
                return null;
            }
        }

        public static bool ValidateRequest(Hashtable communicationData, Hashtable requestData, string gatewayURL)
        {
            m_log.Debug ("[OMECONOMY]: ValidateRequest (cd, rd, " + gatewayURL + ")");
            foreach (DictionaryEntry cd in communicationData) {
                m_log.Debug ("[OMECONOMY]:   cd[" + cd.Key.ToString () + "]=" + cd.Value.ToString ());
            }
            foreach (DictionaryEntry rd in requestData) {
                m_log.Debug ("[OMECONOMY]:   rd[" + rd.Key.ToString () + "]=" + rd.Value.ToString ());
            }
            Hashtable requestDataHashing = (Hashtable)requestData.Clone();
            requestDataHashing.Remove("method");

            UUID regionUUID  = UUID.Parse((string)communicationData["regionUUID"]);
            String nonce  = (string)communicationData["nonce"];
            string notificationID = (string)communicationData["notificationID"];

            Dictionary<string, string> d = new Dictionary<string, string>();
            d.Add("method", "verifyNotification");
            d.Add("notificationID", notificationID);
            d.Add("regionUUID", regionUUID.ToString());
            d.Add("hashValue", hashParameters(requestDataHashing, nonce, regionUUID));
            Dictionary<string, string> response = DoRequest (gatewayURL, d);

            string status = response["status"];
            m_log.Debug ("[OMECONOMY]:   -> " + status);

            if (status == "OK") return true;

            // Sometimes the server just goes braindead on us and somehow loses the key
            // and keeps failing subsequent validate requests.
            // So we fetch a new one if the validate request fails.
            // Unfortunately replaying this validate request with the new key also fails.
            // But at least the next validate request seems to succeed.
            m_log.Warn ("[OMECONOMY]: refetching secret");
            OMBaseModule.InitializeRegion (regionUUID);
            return false;
        }

        public static string GetGatewayURL(string initURL, string name, string moduleVersion, string gatewayEnvironment)
        {

            #region // Debug
#if DEBUG
            m_log.DebugFormat("[OMECONOMY] getGatewayURL({0}, {1}, {2}, {3})",
                initURL, name, moduleVersion, gatewayEnvironment);
#endif
            #endregion

            Dictionary<string, string> d = new Dictionary<string, string>();
            d.Add("moduleName", name);
            d.Add("moduleVersion", moduleVersion);
            d.Add("gatewayEnvironment", gatewayEnvironment);

            Dictionary<string, string> response = CommunicationHelpers.DoRequest(initURL, d);
            string gatewayURL = (string)response["gatewayURL"];

            if (gatewayURL != null)
            {
                m_log.InfoFormat("[OMECONOMY]: GatewayURL: {1}", name, gatewayURL);
            }
            else
            {
                m_log.ErrorFormat("[OMECONOMY]: Could not set the GatewayURL - Please restart or contact the module vendor", name);
            }
            return gatewayURL;
        }
    }
}
