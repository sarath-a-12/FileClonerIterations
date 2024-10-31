﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Networking.Communication;
using Networking;

namespace GroupProjectSE.FileCloning.FileSharing;

public class FileReceiver : FileClonerHeaders, IFileReceiver, INotificationHandler
{
    private const string CurrentModuleName = "FileReceiver";

    // this lock is necessary when writing to files
    private object _fileWriteLock;
    private Dictionary<string, object> _syncLockForSavingResponse;

    private string _myServerAddress;

    // private CommunicatorClient _fileReceiver;
    // CommunicatorServer is much more useful than Communicator Client??
    // for broadcasting messages
    private CommunicatorServer _fileReceiverServer;

    // these fields are only necessary for Receiver
    private const string ReceiverConfigFilePath = ".\\requestConfig.json";
    private const string ResponseOfRequestConfigFilePath = ".\\responseOfRequestConfig.json";
    private const string RequestToSendFilePath = ".\\requestToSend.json";

    private List<string> _requestFilesPathList;

    public FileReceiver() : base(CurrentModuleName)
    {
        _fileWriteLock = new();
        _requestFilesPathList = new();
        _syncLockForSavingResponse = new();

        // for each file to be received from a particular device D
        // creates a new FileReceiver which handles the receiving and saving of the particular file
        // _fileReceiver = new CommunicatorClient();
        _fileReceiverServer = new CommunicatorServer();
        _myServerAddress = _fileReceiverServer.Start();
        _myServerAddress = _myServerAddress.Replace(':', '_');
        _clientIdToSocket = _fileReceiverServer.GetClientList();

        // Subscribe for messages with module name as "FileReceiver"
        // _fileReceiver.Subscribe(CurrentModuleName, this, false);
        _fileReceiverServer.Subscribe(CurrentModuleName, this, false);

        CreateAndCloseFile(ReceiverConfigFilePath);
        //CreateAndCloseFile(ResponseOfRequestConfigFilePath);
        //CreateAndCloseFile(RequestToSendFilePath);


        // when starting, read the config file
        SaveFileRequests();

    }

    /// <summary>
    /// gets the list of files to be cloned and broadcasts the request to all file servers
    /// </summary>
    public void RequestFiles()
    {
        SaveFileRequests();
        // broadcast the request to all file servers
        string sendFileRequests = _serializer.Serialize(_requestFilesPathList);
        // client can't really send broadcast, hence using the server
        _fileReceiverServer.Send(
            GetMessage(FileRequestHeader, sendFileRequests),
            CurrentModuleName, null);
    }

    /// <summary>
    /// After getting responses from the server about the filePath and the timeStamp,
    /// we need to decide which file to be cloned from which server based on the timestamp
    /// </summary>
    public void GenerateDiff()
    {
        // Evans' code should be called from here, as a thread or something
        throw new NotImplementedException();
    }

    public void RequestToCloneFiles()
    {
        // take the requestToSend.json which contains the information
        // about which file to ask from which server

        if (!CreateAndCloseFile(RequestToSendFilePath))
        {
            _logger.Log($"Not able to find {RequestToSendFilePath}");
            return;
        }

        // the file contains the key list of json objects
        // each object is of the form
        // "filePath" : <filePath>
        // "fromWhichServer" : <address>

        string jsonContent = File.ReadAllText(RequestToSendFilePath);
        using JsonDocument doc = JsonDocument.Parse(jsonContent);
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            string? filePath = element.GetProperty(ReceiverConfigFilePathKey).GetString();
            string? fromWhichServer = element.GetProperty(ReceiverConfigFromWhichServerKey).GetString();

            if (filePath == null || fromWhichServer == null)
            {
                continue;
            }

            // send the request to clone this file
            _fileReceiverServer.Send(
                GetMessage(CloneFilesHeader, filePath),
                CurrentModuleName, GetClientId(fromWhichServer)
                );
        }


    }

    /// <summary>
    /// Mentions what to do when data is received
    /// </summary>
    /// <param name="serializedData"></param>
    public void OnDataReceived(string serializedData)
    {
        // after the header contains the serialized data
        string[] serializedDataList = serializedData.Split(':', MessageSplitLength);
        string header = serializedDataList[HeaderIndex];
        string sendToAddress = serializedDataList[AddressIndex];
        string clientId = GetClientId(sendToAddress);

        if (serializedData.StartsWith(AckFileRequestHeader))
        {
            string serializedJsonData = serializedDataList[MessageIndex];

            Thread saveResponseThread = new Thread(() => {
                SaveResponse(serializedJsonData, sendToAddress);
            });
            saveResponseThread.Start();
        }
        else if (serializedData.StartsWith(AckCloneFilesHeader))
        {
            string data = serializedDataList[MessageIndex];

            // format of data here is filePath:count:chunk
            // the below will help in determining the percentage of file Transfer completed
            string[] dataList = data.Split(':', 3);
            string filePath = dataList[0];
            string[] countByTotalNumberOfTransmissions = dataList[1].Split('/', 2);
            long count = long.Parse(countByTotalNumberOfTransmissions[0]);
            long numberOfTransmissionsRequired =
                long.Parse(countByTotalNumberOfTransmissions[1]);

            // format is Address:AckCloneFilesHeader:filePath:count:chunk
            Thread receiveFilesThroughNetwork = new Thread(() => {
                // put the corresponding save file path over here
                ReceiveFileOverNetwork(data);
            });
            receiveFilesThroughNetwork.Start();
        }

    }

    /// <summary>
    /// Saves the response from the file Server `fromWhichServer` and saves it
    /// in the file "fromWhichServer".json
    /// the server will be returning a json file, containing the available files and its
    /// timestamp
    /// </summary>
    /// <param name="data"></param>
    /// <param name="fromWhichServer"></param>
    private void SaveResponse(string data, string fromWhichServer)
    {
        string saveFileName = $"{fromWhichServer}.json";

        if (!_syncLockForSavingResponse.ContainsKey(saveFileName))
        {
            _syncLockForSavingResponse[saveFileName] = new();
        }
        object lockToSaveResponse = _syncLockForSavingResponse[saveFileName];

        if (!CreateAndCloseFile(saveFileName))
        {
            _logger.Log($"Not able to create file {saveFileName}");
            return;
        }

        lock (lockToSaveResponse)
        {
            // data is serialized json
            // saving it in the fileName saveFileName
            File.WriteAllText(saveFileName, data);
        }
    }


    private void ReceiveFileOverNetwork(string data)
    {
        try
        {
            // format of data here is filePath:count:chunk
            string[] dataList = data.Split(':', 3);
            string filePath = dataList[0];
            string[] countByTotalNumberOfTransmissions = dataList[1].Split('/', 2);
            long count = long.Parse(countByTotalNumberOfTransmissions[0]);
            long numberOfTransmissionsRequired =
                long.Parse(countByTotalNumberOfTransmissions[1]);

            string chunk = dataList[2];

            if (count == 0)
            {
                if (!CreateAndCloseFile(filePath))
                {
                    _logger.Log($"Could not create {filePath}");
                    return;
                }
                lock (_fileWriteLock)
                {
                    //false means that it will overwrite onto the file
                    using StreamWriter writer = new StreamWriter(filePath, false);
                    writer.Write(chunk);
                }
            }
            else
            {
                lock (_fileWriteLock)
                {
                    // true implies that it will get appended
                    using StreamWriter writer = new StreamWriter(filePath, true);
                    writer.Write(chunk);
                }

            }
            _logger.Log($"Chunk number {count} written succesfully onto the file.");
        }
        catch (Exception e)
        {
            _logger.Log(e.Message);
        }
    }

    /// <summary>
    ///  reads the config file which contains the list of files to be cloned and 
    ///  saves it in a list
    /// </summary>
    private void SaveFileRequests()
    {
        // check the FILE_REQUEST.json file which contains list of files to be cloned
        // every item in the list is a JSON object with keys being fileName and values being filePath

        _requestFilesPathList = new();
        try
        {
            string jsonContent = File.ReadAllText(ReceiverConfigFilePath);
            using JsonDocument doc = JsonDocument.Parse(jsonContent);

            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                string? filePath = element.GetProperty(ReceiverConfigFilePathKey).GetString();
                if (filePath != null)
                {
                    _requestFilesPathList.Add(filePath);
                }
            }
        }
        catch (FileNotFoundException ex)
        {
            _logger.Log(ex.Message);
            // create and close the file
            CreateAndCloseFile(ReceiverConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.Log(ex.Message);
        }
    }

    /// <summary>
    /// overloads the base functionality since myAddress is known, and thus we don't have to give it every time
    /// when sending a message
    /// </summary>
    /// <param name="header"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    private string GetMessage(string header, string message)
    {
        return GetMessage(_myServerAddress, header, message);
    }

    /// <summary>
    /// extracts ip address and port from the socket
    /// </summary>
    /// <param name="socket"></param>
    /// <returns>
    /// a string in the format IPAddress_Port
    /// </returns>
    private string GetMyAddress(TcpClient socket)
    {
        IPEndPoint? remoteEndPoint = (IPEndPoint?)socket.Client.RemoteEndPoint;
        if (remoteEndPoint == null)
        {
            return "";
        }
        string ipAddress = remoteEndPoint.Address.ToString();
        string port = remoteEndPoint.Port.ToString();
        // using underscores since apparently fileNames cannot have :
        string address = GetConcatenatedAddress(ipAddress, port);
        return address;

    }

    /// <summary>
    /// Helper function to create and close the file
    /// taking care of error handling
    /// creates a new file only if the given file path does not exist
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns>
    /// a boolean indicating if Creation of file was successful
    /// </returns>
    private bool CreateAndCloseFile(string filePath)
    {
        // returns if success or failure
        try
        {
            if (!File.Exists(filePath))
            {
                File.Create(filePath).Close();
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.Write(ex);
        }

        return false;
    }

    public void StopFileReceiver()
    {
        _fileReceiverServer.Stop();
    }

}