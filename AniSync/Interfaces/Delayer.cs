using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniSync.Interfaces
{
    public interface IAsyncDelayer
    {
        Task Delay(TimeSpan timeSpan);
    }
    public class Delayer : IAsyncDelayer
    {
        public async Task Delay(TimeSpan timeSpan)
        {
            if (timeSpan > TimeSpan.Zero)
                await Task.Delay(timeSpan);
        }
    }
}
