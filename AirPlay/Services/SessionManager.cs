using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Enums;

namespace AirPlay.Services.Implementations
{
    public class SessionManager
    {
        private static SessionManager _current = null;
        private ConcurrentDictionary<string, Session> _sessions;

        public static SessionManager Current => _current ?? (_current = new SessionManager());

        private SessionManager()
        {
            _sessions = new ConcurrentDictionary<string, Session>();
        }

        public Task<Session> GetSessionAsync(string key)
        {
            _sessions.TryGetValue(key, out Session _session);
            return Task.FromResult(_session ?? new Session(key));
        }

        public Task CreateOrUpdateSessionAsync(string key, Session session)
        {
            _sessions.AddOrUpdate(key, session, (k, old) => session);
            return Task.CompletedTask;
        }
    }
}
