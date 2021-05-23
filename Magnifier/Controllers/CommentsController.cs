﻿using Magnifier.Models;
using Magnifier.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Magnifier.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CommentsController : ControllerBase
    {
        private readonly CommentService commentService;
        private readonly ReactionService reactionService;
        private readonly HttpClient client;

        public CommentsController(CommentService _commentService, ReactionService _reactionService)
        {
            commentService = _commentService;
            reactionService = _reactionService;
            client = new HttpClient();
        }

        private async Task<ScratchRequestResponse> GetScratchProject(int projectId)
        {
            HttpResponseMessage response = await client.GetAsync($"https://api.scratch.mit.edu/projects/{projectId}");
            var data = await response.Content.ReadAsStringAsync();

            return new ScratchRequestResponse(response, JsonConvert.DeserializeObject<ScratchProject>(data));
        }

        private async Task<ScratchRequestResponse> GetScratchComment(int projectId, int commentId)
        {
            ScratchRequestResponse requestResponse = await GetScratchProject(projectId);

            if (!requestResponse.succeeded)
            {
                return new ScratchRequestResponse(requestResponse.response);
            }

            ScratchProject project = requestResponse.project;

            string projectOwner = project.author.username;

            HttpResponseMessage response = await client.GetAsync($"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{commentId}");
            var data = await response.Content.ReadAsStringAsync();

            return new ScratchRequestResponse(requestResponse.response, _comment: JsonConvert.DeserializeObject<ScratchComment>(data));
        }

        private async Task<ScratchRequestResponse> GetScratchCommentReplies(int projectId, int commentId)
        {
            ScratchRequestResponse requestResponse = await GetScratchProject(projectId);

            if (!requestResponse.succeeded)
            {
                return new ScratchRequestResponse(requestResponse.response);
            }

            ScratchProject project = requestResponse.project;

            string projectOwner = project.author.username;

            requestResponse = await GetScratchComment(projectId, commentId);

            if (!requestResponse.succeeded)
            {
                return new ScratchRequestResponse(requestResponse.response);
            }

            ScratchComment comment = requestResponse.comment;

            HttpResponseMessage response = await client.GetAsync($"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{comment.id}/replies");
            var data = await response.Content.ReadAsStringAsync();

            return new ScratchRequestResponse(requestResponse.response, _comments: JsonConvert.DeserializeObject<List<ScratchComment>>(data));
        }

        [HttpGet("{projectId}/{commentId}")]
        public async Task<ActionResult> GetCommentAsync(int projectId, int commentId)
        {
            if (commentService.Get(commentId) == null)
            {
                ScratchRequestResponse requestResponse = await GetScratchComment(projectId, commentId);

                if (!requestResponse.succeeded)
                {
                    return NotFound(requestResponse.statusCode.ToString());
                }

                ScratchComment comment = requestResponse.comment;

                requestResponse = await GetScratchCommentReplies(projectId, comment.id);

                if (!requestResponse.succeeded)
                {
                    return NotFound(requestResponse.statusCode.ToString());
                }

                var scratchCommentReplies = requestResponse.comments;

                List<int> replies = new List<int>();

                foreach (ScratchComment scratchComment in scratchCommentReplies)
                {
                    commentService.Create(new Comment(scratchComment.id, scratchComment, new List<int>()));
                    replies.Add(scratchComment.id);
                }

                commentService.Create(new Comment(comment.id, comment, replies));
            }

            return Ok(commentService.Get(commentId));
        }

        [HttpGet("projects/{projectId}/{page}")]
        public async System.Threading.Tasks.Task<ActionResult> GetProjectCommentsAsync(int projectId, int page)
        {
            ScratchRequestResponse requestResponse = await GetScratchProject(projectId);

            if (!requestResponse.succeeded)
            {
                return NotFound(requestResponse.statusCode.ToString());
            }

            ScratchProject project = requestResponse.project;

            if (project.author == null)
            {
                return BadRequest("that project doesnt exist");
            }

            string projectOwner = project.author.username;

            var response = await client.GetAsync($"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments?offset={(page - 1) * 20}");
            var data = await response.Content.ReadAsStringAsync();

            List<ScratchComment> comments = JsonConvert.DeserializeObject<List<ScratchComment>>(data);

            List<Comment> dbComments = commentService.Get();

            foreach (ScratchComment comment in comments)
            {
                requestResponse = await GetScratchCommentReplies(projectId, comment.id);

                if (!requestResponse.succeeded)
                {
                    return NotFound(requestResponse.statusCode.ToString());
                }

                var scratchCommentReplies = requestResponse.comments;

                List<int> replies = new List<int>();

                foreach (ScratchComment scratchComment in scratchCommentReplies)
                {
                    commentService.Create(new Comment(scratchComment.id, scratchComment, new List<int>()));
                    replies.Add(scratchComment.id);
                }

                if (dbComments.Find(dbComment => dbComment.commentId == comment.id) == null)
                {
                    commentService.Create(new Comment(comment.id, comment, replies));
                }
            }

            dbComments = commentService.Get();

            List<Comment> matchingComments = commentService.Get().FindAll(comment => comments.Find(scratchComment => scratchComment.id == comment.commentId) != null);

            matchingComments = matchingComments
                .Where(p => p.comment.datetime_created.HasValue)
                .OrderBy(p => p.comment.datetime_created.Value)
                .Reverse()
                .ToList();

            return Ok(System.Text.Json.JsonSerializer.Serialize(matchingComments));
        }

        [HttpPut("{projectId}/{commentId}/reactions")]
        [Authorize]
        public async Task<ActionResult> PutReactionAsync(int projectId, int commentId, string reaction)
        {
            ScratchRequestResponse requestResponse = await GetScratchProject(projectId);

            if (!requestResponse.succeeded)
            {
                return NotFound(requestResponse.statusCode.ToString());
            }

            ScratchProject project = requestResponse.project;

            if (project.author == null)
            {
                return BadRequest("that project doesnt exist");
            }

            string projectOwner = project.author.username;

            Comment comment;

            string projectUrl = $"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}";

            var response = await client.GetAsync(projectUrl);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound();
            }

            if (commentService.Get(commentId) == null)
            {
                string commentUrl = $"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{commentId}";

                response = await client.GetAsync(commentUrl);

                var repliesResponse = await client.GetAsync($"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{commentId}/replies");
                var data = await repliesResponse.Content.ReadAsStringAsync();

                var scratchCommentReplies = JsonConvert.DeserializeObject<List<ScratchComment>>(data);

                List<int> replies = new List<int>();

                foreach (ScratchComment scratchComment in scratchCommentReplies)
                {
                    commentService.Create(new Comment(scratchComment.id, scratchComment, new List<int>()));
                    replies.Add(scratchComment.id);
                }

                comment = commentService.Create(new Comment(commentId, JsonConvert.DeserializeObject<ScratchComment>(await response.Content.ReadAsStringAsync()), replies));
            }
            else
            {
                comment = commentService.Get(commentId);
            }

            if (comment.reactions == null)
            {
                comment.reactions = new List<UserReaction>();
            }

            if (reactionService.Get(reaction) != null)
            {
                string username = HttpContext.User.Claims.ToList().Find(claim => claim.Type == "username").Value;

                UserReaction userReaction = new UserReaction(username, reaction);

                if (comment.reactions.Find(userReaction => userReaction.user == username && userReaction.reaction == reaction) == null)
                {
                    comment.reactions.Add(userReaction);
                }
                else
                {
                    comment.reactions.Remove(comment.reactions.Find(userReaction => userReaction.user == username && userReaction.reaction == reaction));
                }

                commentService.Update(commentId, comment);

                return Accepted();
            }

            return NotFound("that reaction doesnt exist");
        }

        [HttpPut("{projectId}/{commentId}/pin")]
        [Authorize]
        public async Task<ActionResult> PinCommentAsync(int projectId, int commentId, bool pin = true)
        {
            ScratchRequestResponse requestResponse = await GetScratchProject(projectId);

            if (!requestResponse.succeeded)
            {
                return NotFound(requestResponse.statusCode.ToString());
            }

            ScratchProject project = requestResponse.project;

            if (project.author == null)
            {
                return BadRequest("that project doesnt exist");
            }

            string projectOwner = project.author.username;

            Comment comment;

            if (commentService.Get(commentId) == null)
            {
                string commentUrl = $"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{commentId}";

                var client = new HttpClient();
                var response = await client.GetAsync(commentUrl);

                var repliesResponse = await client.GetAsync($"https://api.scratch.mit.edu/users/{projectOwner}/projects/{projectId}/comments/{commentId}/replies");
                var data  = await repliesResponse.Content.ReadAsStringAsync();

                var scratchCommentReplies = JsonConvert.DeserializeObject<List<ScratchComment>>(data);

                List<int> replies = new List<int>();

                foreach (ScratchComment scratchComment in scratchCommentReplies)
                {
                    commentService.Create(new Comment(scratchComment.id, scratchComment, new List<int>()));
                    replies.Add(scratchComment.id);
                }

                comment = commentService.Create(new Comment(commentId, JsonConvert.DeserializeObject<ScratchComment>(await response.Content.ReadAsStringAsync()), replies));
            }
            else
            {
                comment = commentService.Get(commentId);
            }

            comment.isPinned = pin;

            commentService.Update(commentId, comment);

            return Accepted();
        }
    }
}