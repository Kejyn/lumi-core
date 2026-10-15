using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lumivale.Core.SceneFlow
{
    public sealed class UnitySceneLoader : ISceneLoader
    {
        public bool CanLoadScene(string scenePath)
        {
            return Application.CanStreamedLevelBeLoaded(scenePath);
        }

        public ISceneLoadOperation BeginLoad(SceneLoadRequest request)
        {
            AsyncOperation operation = SceneManager.LoadSceneAsync(request.ScenePath, request.Mode);
            if (operation == null)
            {
                throw new InvalidOperationException($"Unity did not create a load operation for '{request.ScenePath}'.");
            }

            return new UnitySceneLoadOperation(operation);
        }

        private sealed class UnitySceneLoadOperation : ISceneLoadOperation
        {
            private readonly AsyncOperation _operation;

            public UnitySceneLoadOperation(AsyncOperation operation)
            {
                _operation = operation;
            }

            public float Progress => _operation.progress;
            public bool IsDone => _operation.isDone;

            public void AllowActivation()
            {
                _operation.allowSceneActivation = true;
            }

            public async Task WaitForNextFrameAsync()
            {
                await Awaitable.NextFrameAsync();
            }
        }
    }
}
