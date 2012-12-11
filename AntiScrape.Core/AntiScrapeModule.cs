﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Threading;
using System.Net;
using System.Web.UI;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Practices.Unity;
using UnityConfiguration;
using AntiScrape.Core.IoC;
using AntiScrape.Core.Interfaces;
using System.Security.Cryptography;
using AntiScrape.Support;

namespace AntiScrape.Core
{
    public class AntiScrapeModule : IHttpModule
    {
        private IDataStorage _storage;
        private readonly byte[] _saltBytes;
        private readonly AntiScrapeConfiguration _settings;
        private readonly Func<object, HttpApplicationBase> _getApplicationBase;
        
        public AntiScrapeModule()
            : this(AntiScrapeConfiguration.Settings, 
                    obj => new HttpApplicationWrapper(obj as HttpApplication))
        { }

        public AntiScrapeModule(AntiScrapeConfiguration settings, 
                                Func<object, HttpApplicationBase> getApplicationBase)
        {
            _settings = settings;

            _getApplicationBase = getApplicationBase;

            _saltBytes = Encoding.Unicode.GetBytes(settings.ClassNameSalt);
        }

        public void Dispose() { }

        public void Init(HttpApplication context)
        {
            Init(_getApplicationBase(context));
        }

        public void Init(HttpApplicationBase context)
        {
            Debug.WriteLine("AntiScrapeModule.Init");

            using (var container = new UnityContainer())
            {
                container.Configure(x =>
                {
                    x.AddRegistry<IoCRegistry>();
                });

                _storage = container.Resolve<IDataStorage>();
            }

            context.BeginRequest += OnBeginRequest;
            context.PostMapRequestHandler += OnPostMapRequestHandler;
        }

        void OnPostMapRequestHandler(object sender, EventArgs e)
        {
            Debug.WriteLine("AntiScrapeModule.OnPostMapRequestHandler");

            var application = _getApplicationBase(sender);

            var context = application.Context;
            var page = context.CurrentHandler as Page;
            if (page != null)
            {
                page.PreRenderComplete += OnPreRenderComplete;
            }
        }

        void OnPreRenderComplete(object sender, EventArgs e)
        {
            Debug.WriteLine("AntiScrapeModule.OnPreRenderComplete");

            Page page = sender as Page;

            if (page != null)
            {
                var data = new List<byte>();

                data.AddRange(_saltBytes);
                data.AddRange(Encoding.Unicode.GetBytes(page.AppRelativeVirtualPath));

                byte[] hash;
                using (var sha1 = new SHA1Managed())
                {
                    hash = sha1.ComputeHash(data.ToArray());
                }

                var className = HttpServerUtility.UrlTokenEncode(hash);

                Debug.WriteLine(string.Format("class name: {0}", className));

                var linkUri = new Uri(_settings.HoneypotRelativeUrl, UriKind.Relative);

                Debug.WriteLine(string.Format("Link Uri: {0}", linkUri));

                //add css for link
                page.Header.Controls.Add(new LiteralControl(string.Format("<style type=\"text/css\">.{0} {{ display: none; }}</style>", className)));

                //add link
                page.Form.Controls.Add(new LiteralControl(string.Format("<a class=\"{0}\" href=\"{1}\">{1}</a>", className, linkUri)));
            }
        }

        void OnBeginRequest(object sender, EventArgs e)
        {
            Debug.WriteLine("AntiScrapeModule.OnBeginRequest");

            var application = _getApplicationBase(sender);

            if (application != null)
            {                
                if (application.Context.Request.Url.AbsolutePath.Contains(_settings.HoneypotRelativeUrl))
                {
                    Debug.WriteLine(string.Format("Request.Url.AbsolutePath '{0}' contains '{1}'", 
                                        application.Context.Request.Url.AbsolutePath, 
                                        _settings.HoneypotRelativeUrl));

                    _storage.StoreScrapingRequest(application.Context.Request);
                }
                else if (!_storage.IsKnownScraper(application.Context.Request))
                    return;

                Debug.WriteLine("Known Scraper!");

                var content = "<html><head><title></title></head><body></body></html>";

                if(!string.IsNullOrWhiteSpace(_settings.ContentVirtualPath))
                {
                    var contentPath = application.Context.Server.MapPath(_settings.ContentVirtualPath);

                    if (File.Exists(contentPath))
                    {
                        content = File.ReadAllText(contentPath);
                    }
                }

                var action = _settings.Action;

                Debug.WriteLine(string.Format("AntiScrapeConfiguration.Settings.Action = {0}", action));

                switch (action)
                {
                    case AntiScrapeAction.Delay:

                        var rng = new Random();

                        var delay = rng.Next(_settings.MinDelay, _settings.MaxDelay);

                        Debug.WriteLine(string.Format("Waiting {0}", delay));

                        Thread.Sleep(delay);

                        //continue processing request

                        break;

                    case AntiScrapeAction.Error:

                        Debug.WriteLine(string.Format("AntiScrapeConfiguration.Settings.ErrorCode = {0}", _settings.ErrorCode));

                        application.Context.Response.StatusCode = (int)_settings.ErrorCode;
                        application.Context.Response.SuppressContent = true;
                        application.Context.Response.End();

                        break;

                    case AntiScrapeAction.EmptyResponse:

                        application.Context.Response.StatusCode = (int)HttpStatusCode.OK;
                        application.Context.Response.SuppressContent = true;
                        application.Context.Response.End();

                        break;

                    case AntiScrapeAction.CustomResponse:

                        application.Context.Response.StatusCode = (int)HttpStatusCode.OK;
                        application.Context.Response.Write(content);
                        application.Context.Response.End();

                        break;

                    case AntiScrapeAction.None:
                    default:
                        //do nothing
                        break;
                }
            }
        }
    }
}
