using System;

namespace KimNotes.utils
{
    internal sealed class SyncSchedule
    {
        internal static readonly TimeSpan QuietPeriod=TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan MaximumUploadWait=TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan PollInterval=TimeSpan.FromSeconds(60);
        private DateTime pollAt=DateTime.MinValue,notBefore=DateTime.MinValue;
        private DateTime? firstPending,uploadAt;
        private bool forceUpload=true;

        internal bool HasPendingUpload => forceUpload || uploadAt.HasValue;
        internal bool UploadDue(DateTime now) => forceUpload || uploadAt.HasValue && now>=uploadAt.Value;
        internal bool IsDue(DateTime now) => now>=notBefore && (now>=pollAt || UploadDue(now));
        internal void Saved(DateTime now)
        {
            if(!firstPending.HasValue)firstPending=now;
            uploadAt=now+QuietPeriod;
            var deadline=firstPending.Value+MaximumUploadWait;
            if(uploadAt>deadline)uploadAt=deadline;
        }
        internal void Discovered(DateTime now) {if(!HasPendingUpload)Saved(now);}
        internal void Begin(bool upload)
        {
            if(!upload)return;
            // Later saves belong to the next round and get their own deadline.
            firstPending=uploadAt=null;forceUpload=false;
        }
        internal void Complete(DateTime now,TimeSpan? retry=null)
        {
            pollAt=now+(retry ?? PollInterval);
            notBefore=retry.HasValue ? pollAt : DateTime.MinValue;
            if(retry.HasValue)forceUpload=true;
        }
        internal void Reset(){pollAt=notBefore=DateTime.MinValue;firstPending=uploadAt=null;forceUpload=true;}
    }
}
