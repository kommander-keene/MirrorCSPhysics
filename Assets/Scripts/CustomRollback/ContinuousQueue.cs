
/// <summary>
/// Defines a queue stores states at certain timestamps.
/// Used to perform server-side rollbacks.
/// 
/// Queue size should be extremely small to prevent bloat and computer overworking
/// 
/// Ideally:
/// Add is O(1)
/// Query is O(log(n)) based off server time
/// Remove is O(1)
/// </summary>
public struct ContinuousQueue
{

}