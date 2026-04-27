namespace Example.Test
{
    public class Tests
    {
        [Test]
        public async Task Basic()
        {
            await 2.ToString().Should().IsEqualTo("2");
            await Assert.That(2.ToString()).IsEqualTo("2");
        }
    }
}
