﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using SupervisorWebService.Five9SupervisorService;
using SupervisorWebService.Five9AdminService;
using System.ServiceModel;
using System.Threading;
using System.ServiceModel.Security;

namespace SupervisorWebService
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            instance = this;
            InitializeComponent();
            Application.ApplicationExit += new EventHandler(Application_ApplicationExit);
        }

        private static Form1 instance = null;

        private WsSupervisorClient supervisorClient = null;
        private WsAdminClient adminClient = null;
        private long lastTimestamp = 0;
        private Dictionary<string, int> agentStateColumns = new Dictionary<string, int>();
        private Thread updateThread;

        private void setSessionParameters_Click(object sender, EventArgs e)
        {
            supervisorClient = new WsSupervisorClient();

            // Add our AuthHeaderInserter behavior to the client endpoint
            // this will invoke our behavior before every send so that
            // we can insert the "Authorization" HTTP header before it is sent.
            AuthHeaderInserter inserter = new AuthHeaderInserter();
            inserter.Username = txtUsername.Text;
            inserter.Password = txtPassword.Text;
            supervisorClient.Endpoint.Behaviors.Add(new AuthHeaderBehavior(inserter));

            setSessionParameters sessionParams = new setSessionParameters();
            sessionParams.viewSettings = new viewSettings
            {
                forceLogoutSession = true,
                rollingPeriodSpecified = true,
                rollingPeriod = rollingPeriod.Hour1,
                shiftStart = 8 * 60 * 60 * 1000, // 8AM in mS
                statisticsRangeSpecified = true,
                statisticsRange = statisticsRange.CurrentDay,
                // use local time zone
                timeZone = int.Parse(String.Format("{0:0}",
                    TimeZone.CurrentTimeZone.GetUtcOffset(DateTime.Now).TotalHours))
            };

            try
            {
                supervisorClient.setSessionParameters(sessionParams);

                Log("Client state: " + supervisorClient.State);
                this.setSessionParameters.Enabled = false;
                this.getCallCounterStates.Enabled = true;
                this.getColumnNames.Enabled = true;
                this.closeSession.Enabled = true;
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }
        }

        private void getCallCounterStates_Click(object sender, EventArgs e)
        {
            try
            {
                Five9SupervisorService.limitTimeoutState[] resp
                    = supervisorClient.getCallCountersState(
                    new Five9SupervisorService.getCallCountersState());

                Log("Call Counters:");
                foreach (Five9SupervisorService.limitTimeoutState timeout in resp)
                {
                    Log(" timeout: " + timeout.timeout);
                    foreach (Five9SupervisorService.callCounterState state in timeout.callCounterStates)
                    {
                        Log("  " + state.operationType.ToString() + ": " + state.value + " / " + state.limit);
                    }
                }
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }
        }

        private void getColumnNames_Click(object sender, EventArgs e)
        {
            Five9SupervisorService.getColumnNames columnNames 
                = new Five9SupervisorService.getColumnNames();
            columnNames.statisticTypeSpecified = true;
            columnNames.statisticType = statisticType.AgentState;

            try
            {
                Five9SupervisorService.getColumnNamesResponse resp 
                    = supervisorClient.getColumnNames(columnNames);

                Log("getColumnNames type: " + columnNames.statisticType.ToString());
                agentStateColumns.Clear();

                int i = 0;
                foreach (String c in resp.@return.values)
                {
                    Log(" [" + i + "] - " + c);
                    // create index dictionary for column names
                    agentStateColumns.Add(c, i++);
                }

                this.getStatistics.Enabled = true;
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }
        }

        private void getStatistics_Click(object sender, EventArgs e)
        {            
            Five9SupervisorService.getStatistics statistics
                = new Five9SupervisorService.getStatistics();
            statistics.statisticTypeSpecified = true;
            statistics.statisticType = statisticType.AgentState;
            statistics.columnNames = null; // all columns, please

            try
            {
                Five9SupervisorService.getStatisticsResponse resp
                    = supervisorClient.getStatistics(statistics);

                Five9SupervisorService.statistics statistics_return = resp.@return;
                // track last update timestamp
                lastTimestamp = statistics_return.timestamp;

                Log("getStatistics type: " + statistics_return.type.ToString());
                // display usernmae and current state as an example
                int usernameIdx = agentStateColumns["Username"];
                int stateIdx = agentStateColumns["State"];
                foreach (row r in statistics_return.rows)
                {
                    Log(r.values[usernameIdx] + " - State: " + r.values[stateIdx]);
                }

                this.getStatisticsUpdate.Enabled = true;
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }
        }

        private void getStatisticsUpdate_Click(object sender, EventArgs e)
        {
            Log("getStatisticsUpdate: starting thread");
            GetStatisticsUpdateThread t = new GetStatisticsUpdateThread(
                this.supervisorClient, this.lastTimestamp);
            t.LastTimestampChanged += new GetStatisticsUpdateThread
                .LastTimestampChangedEventHandler(t_LastTimestampChanged);
            t.StatisticChanged += new GetStatisticsUpdateThread
                .StatisticChangedEventHandler(t_StatisticChanged);

            updateThread = new Thread(new ThreadStart(t.Run));
            updateThread.Start();
            this.getStatisticsUpdate.Enabled = false;            
        }

        void t_StatisticChanged(GetStatisticsUpdateThread.StatisticChangedEventArgs e)
        {
            Log("getStatisticsUpdate type: " + e.Update.type.ToString());
            // display username and current state as an example
            int usernameIdx = this.agentStateColumns["Username"];
            int stateIdx = this.agentStateColumns["State"];
            foreach (itemUpdate item in e.Update.dataUpdate)
            {
                // in example, only interested in current state
                if (item.columnName.Equals("State"))
                {
                    Log(item.objectName + " - " + item.columnName + ": " + item.columnValue);
                }
            }
        }

        void t_LastTimestampChanged(GetStatisticsUpdateThread.LastTimestampChangedEventArgs e)
        {
            this.lastTimestamp = e.Timestamp;
        }

        private void closeSession_Click(object sender, EventArgs e)
        {
            if (updateThread != null && updateThread.IsAlive)
            {
                Log("Stopping update thread.");
                updateThread.Abort();
            }

            try
            {
                supervisorClient.closeSession(new Five9SupervisorService.closeSession());
                Log("Supervisor session closed.");
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }

            this.setSessionParameters.Enabled = true;
            this.getCallCounterStates.Enabled = false;
            this.getColumnNames.Enabled = false;
            this.getStatistics.Enabled = false;
            this.getStatisticsUpdate.Enabled = false;
            this.closeSession.Enabled = false;

        }

        private void getUserInfo_Click(object sender, EventArgs e)
        {
            adminClient = new WsAdminClient();

            // Add our AuthHeaderInserter behavior to the client endpoint
            // this will invoke our behavior before every send so that
            // we can insert the "Authorization" HTTP header before it is sent.
            AuthHeaderInserter inserter = new AuthHeaderInserter();
            inserter.Username = txtUsername.Text;
            inserter.Password = txtPassword.Text;
            adminClient.Endpoint.Behaviors.Add(new AuthHeaderBehavior(inserter));

            getUserInfo uiRequest = new getUserInfo();
            uiRequest.userName = txtUsername.Text;

            try
            {
                Five9AdminService.getUserInfoResponse resp
                    = adminClient.getUserInfo(uiRequest);

                Log("userInfo:");
                Log(" id: " + resp.@return.generalInfo.id);
                Log(" firstName: " + resp.@return.generalInfo.firstName);
                Log(" lastName: " + resp.@return.generalInfo.lastName);
                Log(" startDate: " + resp.@return.generalInfo.startDate);

                this.closeSession2.Enabled = true;
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }
        }

        private void closeSession2_Click(object sender, EventArgs e)
        {
            try
            {
                adminClient.closeSession(new Five9AdminService.closeSession());
                Log("Admin session closed.");
            }
            catch (MessageSecurityException mse)
            {
                Log("MessageSecurityException: " + mse.Message);
            }
            catch (FaultException ex)
            {
                Log("FaultException: Code: " + ex.Code + ", Reason: " + ex.Reason);
            }

            this.getUserInfo.Enabled = true;
            this.closeSession2.Enabled = false;
        }

        delegate void LogCallback(string text);

        public static void Log(string msg)
        {
            if (instance.textOutput.InvokeRequired)
            {
                LogCallback c = new LogCallback(Log);
                try
                {
                    instance.Invoke(c, new object[] { msg });
                }
                catch (Exception e) { }
            }
            else
            {
                instance.textOutput.Text += msg + "\r\n";
            }
        }

        private void clear_Click(object sender, EventArgs e)
        {
            textOutput.Text = "";
        }

        
        public void Application_ApplicationExit(object sender, EventArgs e)
        {
            this.closeSession.PerformClick();
            this.closeSession2.PerformClick();
        }
    }
}
