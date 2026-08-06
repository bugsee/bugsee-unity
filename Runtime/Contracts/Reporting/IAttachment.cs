namespace Bugsee.Contracts.Reporting
{
    public interface IAttachment
    {
        string Name { get; set; }
        string Filename { get; set; }
        string MimeType { get; set; }
        void SetData(byte[] data);
        void SetData(string text);
    }
}
